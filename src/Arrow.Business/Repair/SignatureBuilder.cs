using System.Text.RegularExpressions;
using Arrow.Common.Verification;

namespace Arrow.Business.Repair;

/// <summary>
/// Normalized error signatures (P04 §4.1): the kind of error and a name, without line numbers, paths or values, so that a
/// repeated error is recognized. The first error is used, preferring one in the generated file.
/// </summary>
public static class SignatureBuilder
{
    /// <summary><c>COMPILE | &lt;javac kind&gt; | &lt;symbol kind or -&gt;</c>.</summary>
    public static string Compile(IReadOnlyList<CompileError> errors)
    {
        var first = errors.FirstOrDefault(e => e.InGeneratedFile) ?? errors.FirstOrDefault();
        return first is null ? "COMPILE | other | -" : $"COMPILE | {first.Kind} | {first.SymbolKind ?? "-"}";
    }

    /// <summary><c>UNKNOWN | &lt;plugin or task&gt; | -</c>.</summary>
    public static string Unknown(string? log) => $"UNKNOWN | {BuildErrorExtractor.FailingStep(log ?? "")} | -";

    /// <summary><c>DISCOVERY | no tests executed | -</c>.</summary>
    public static string Discovery() => "DISCOVERY | no tests executed | -";

    /// <summary><c>RUNTIME | &lt;short exception&gt; | &lt;Class.method of the first project frame&gt;</c>.</summary>
    public static string Runtime(IReadOnlyList<FailureInfo> failures)
    {
        var first = failures.FirstOrDefault(f => !TestResultClassifier.IsAssertion(f.Type, f.Message)) ?? failures.FirstOrDefault();
        return first is null ? "RUNTIME | unknown | -" : $"RUNTIME | {TestResultClassifier.ShortName(first.Type)} | {first.Frame ?? "-"}";
    }

    /// <summary><c>RUNTIME | &lt;plugin or task&gt; | -</c>: the run of step 2 failed without a report (JVM crash, <c>System.exit</c>; P04 §3.2).</summary>
    public static string RuntimeCrash(string? log) => $"RUNTIME | {BuildErrorExtractor.FailingStep(log ?? "")} | -";

    /// <summary><c>ASSERTION | &lt;short exception&gt; | &lt;first failing method&gt;</c>.</summary>
    public static string Assertion(IReadOnlyList<FailureInfo> failures)
    {
        var first = failures.FirstOrDefault();
        return first is null ? "ASSERTION | unknown | -" : $"ASSERTION | {TestResultClassifier.ShortName(first.Type)} | {first.Method}";
    }

    /// <summary>
    /// <c>REGRESSION | existing tests fail | ATest,BTest</c>: short names of the broken classes, the generated one included for
    /// case (c), without <c>$Inner</c>, sorted (R03 §15.1).
    /// </summary>
    public static string Regression(IReadOnlyList<RegressionInfo> regressions)
    {
        var classes = regressions
            .Select(r => r.Test.Split('#')[0])
            .Select(c => TestResultClassifier.ShortName(c.Split('$')[0]))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return $"REGRESSION | existing tests fail | {string.Join(',', classes)}";
    }
}

/// <summary>The build error block of UNKNOWN (P04 §4.2): <c>Failed to execute goal</c> (Maven) or <c>What went wrong</c> (Gradle), with <c>Caused by:</c>.</summary>
public static partial class BuildErrorExtractor
{
    // from ARROW_Framework/src/Arrow.Service/Block3/SignatureBuilder.cs
    [GeneratedRegex(@"Failed to execute goal (?<group>[\w.\-]+):(?<artifact>[\w.\-]+):(?<version>[\w.\-]+):(?<goal>[\w.\-]+)")]
    private static partial Regex MavenGoal();

    [GeneratedRegex(@"Execution failed for task '(?<task>[^']+)'")]
    private static partial Regex GradleTask();

    // from ARROW_Framework/src/Arrow.Service/Block3/SignatureBuilder.cs
    private static readonly string[] NoiseMarkers =
    [
        "-> [Help", "Re-run Maven", "For more information about the errors", "To see the full stack trace", "[Help 1]",
        "After correcting the problems", "mvn <args> -rf", "Run with --stacktrace", "Run with --info", "Run with --debug",
        "Run with --scan", "Get more help at", "* Try:",
    ];

    /// <summary>The failing Maven plugin (<c>maven-enforcer-plugin</c>) or Gradle task (<c>compileTestJava</c>); <c>build</c> when unknown.</summary>
    public static string FailingStep(string log)
    {
        if (MavenGoal().Match(log) is { Success: true } goal)
        {
            return goal.Groups["artifact"].Value;
        }

        if (GradleTask().Match(log) is { Success: true } task)
        {
            var name = task.Groups["task"].Value;
            return name[(name.LastIndexOf(':') + 1)..];
        }

        return log.Contains("Failed to execute goal", StringComparison.Ordinal) ? "maven" : "build";
    }

    /// <summary>
    /// At most <paramref name="maxLines"/> lines of the build error, without repeated lines, without the advice of Maven and
    /// Gradle, and with the workspace paths made relative.
    /// </summary>
    /// <param name="log">The build log.</param>
    /// <param name="workspaceRoot">Root of the workspace copy, removed from paths; null to keep them.</param>
    /// <param name="maxLines">Line limit.</param>
    public static string Excerpt(string log, string? workspaceRoot, int maxLines = 60)
    {
        var picked = new List<string>();
        var inBlock = false;
        foreach (var raw in log.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Contains("Failed to execute goal", StringComparison.Ordinal) || line.StartsWith("* What went wrong", StringComparison.Ordinal))
            {
                inBlock = true;
            }
            else if (line.StartsWith("* Try:", StringComparison.Ordinal) || line.StartsWith("BUILD FAILED", StringComparison.Ordinal) || line.StartsWith("[INFO]", StringComparison.Ordinal))
            {
                inBlock = false;
            }

            var keep = (inBlock && (line.StartsWith("[ERROR]", StringComparison.Ordinal) || !line.StartsWith('[')))
                       || line.TrimStart().StartsWith("Caused by:", StringComparison.Ordinal);
            if (keep && line.Trim().Length > 0 && line.Trim() != "[ERROR]" && !NoiseMarkers.Any(m => line.Contains(m, StringComparison.Ordinal)))
            {
                picked.Add(line);
            }
        }

        var joined = string.Join('\n', picked.Distinct(StringComparer.Ordinal).Take(maxLines));
        return workspaceRoot is null ? joined : StripRoot(joined, workspaceRoot);
    }

    /// <summary>Removes the absolute path of the workspace copy: with <c>/</c>, with <c>\</c> and in the <c>/D:/…</c> form of Maven on Windows.</summary>
    public static string StripRoot(string text, string workspaceRoot)
    {
        var root = workspaceRoot.TrimEnd('/', '\\');
        var forward = root.Replace('\\', '/');
        return text.Replace(root + "/", "", StringComparison.Ordinal)
            .Replace(root + "\\", "", StringComparison.Ordinal)
            .Replace("/" + forward + "/", "", StringComparison.Ordinal)
            .Replace(forward + "/", "", StringComparison.Ordinal)
            .Replace(root, ".", StringComparison.Ordinal);
    }
}
