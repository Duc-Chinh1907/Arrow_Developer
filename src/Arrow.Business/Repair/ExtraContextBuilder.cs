using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Arrow.Business.Analysis;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Verification;
using Arrow.DataAccess.JavaHelper;

namespace Arrow.Business.Repair;

/// <summary>The <c>extra_context</c> of a level 2 or 3 prompt, with the parts that could not be built.</summary>
/// <param name="Text">At most 200 lines; empty when there is nothing to add.</param>
/// <param name="Warnings">Why a part is missing (java-helper failed); written to <c>decision.json</c> as <c>extra_context_warnings</c>.</param>
public sealed record ExtraContext(string Text, IReadOnlyList<string> Warnings);

/// <summary>
/// <c>extra_context</c> by the state of the checkpoint (P04 §6.5). A java-helper failure never stops the round: that part is left
/// out and a warning is returned.
/// </summary>
public sealed partial class ExtraContextBuilder(IJavaHelperProvider javaHelpers)
{
    /// <summary>Lines of <c>extra_context</c>, the <c>… (truncated)</c> line included.</summary>
    public const int MaxLines = 200;

    /// <summary>Packages listed for <c>package does not exist</c>.</summary>
    public const int MaxPackages = 60;

    /// <summary>Lines of build error for UNKNOWN.</summary>
    public const int MaxBuildErrorLines = 120;

    // Classes in "location: class X", "variable s of type X".
    [GeneratedRegex(@"(?:class|interface|enum|record|of type)\s+(?<name>[\p{L}_$][\p{L}\p{N}_$.]*)")]
    private static partial Regex TypeMention();

    // The four message forms of R03 §6.5 that name a class.
    [GeneratedRegex(@"^constructor\s+\S+\s+in\s+(?:class|enum|record)\s+(?<name>[\p{L}_$][\p{L}\p{N}_$.]*)")]
    private static partial Regex ConstructorIn();

    [GeneratedRegex(@"has private access in\s+(?<name>[\p{L}_$][\p{L}\p{N}_$.]*)")]
    private static partial Regex PrivateAccessIn();

    [GeneratedRegex(@"^(?<name>[\p{L}_$][\p{L}\p{N}_$.]*)\s+is not public in\b")]
    private static partial Regex NotPublic();

    [GeneratedRegex(@"^(?<name>[\p{L}_$][\p{L}\p{N}_$.]*)\s+is abstract; cannot be instantiated")]
    private static partial Regex AbstractClass();

    // from ARROW_Framework/src/Arrow.Service/Block3/ExtraContextBuilder.cs
    [GeneratedRegex(@"expected:?\s*<(?<e>.*?)>\s*but was:?\s*<(?<a>.*?)>", RegexOptions.Singleline)]
    private static partial Regex ExpectedAngle();

    [GeneratedRegex(@"expected:?\s*(?<e>.+?)\s+but was:?\s*(?<a>.+)", RegexOptions.Singleline)]
    private static partial Regex ExpectedPlain();

    [GeneratedRegex(@"System\.(setProperty|clearProperty|setOut|setErr|setIn)\b|Locale\.setDefault|TimeZone\.setDefault|new\s+File\s*\(|Files\.\w+\s*\(|FileWriter|FileOutputStream")]
    private static partial Regex SharedState();

    /// <summary>The <c>extra_context</c> for <paramref name="best"/>, the checkpoint the next round repairs.</summary>
    public async Task<ExtraContext> BuildAsync(ChainVersion best, TaskContext task, CancellationToken ct)
    {
        var warnings = new List<string>();
        var text = best.State switch
        {
            VerificationState.Compile => await CompileAsync(best, task, warnings, ct).ConfigureAwait(false),
            VerificationState.Unknown => Unknown(best.Verify, task.Metadata),
            VerificationState.Discovery => Discovery(task.Metadata),
            VerificationState.Runtime => await RuntimeAsync(best, task, warnings, ct).ConfigureAwait(false),
            VerificationState.Assertion => await AssertionAsync(best, task, warnings, ct).ConfigureAwait(false),
            VerificationState.Regression => await RegressionAsync(best, task, warnings, ct).ConfigureAwait(false),
            _ => "",
        };
        return new ExtraContext(Truncate(text.TrimEnd('\n')), warnings);
    }

    /// <summary>At most <see cref="MaxLines"/> lines; the parts come in the order of the table of P04 §6.5, so the tail is cut.</summary>
    public static string Truncate(string text, int maxLines = MaxLines)
    {
        var lines = text.Split('\n');
        return lines.Length <= maxLines ? text : string.Join('\n', lines.Take(maxLines - 1)) + "\n… (truncated)";
    }

    // ── COMPILE ─────────────────────────────────────────────────────────

    private async Task<string> CompileAsync(ChainVersion best, TaskContext task, List<string> warnings, CancellationToken ct)
    {
        var m = task.Metadata;
        var errors = best.Verify.CompileErrors.Where(e => e.InGeneratedFile).ToList();
        var mentioned = MentionedClasses(errors, m.Cut.Fqn, best.Candidate.TestFqn);

        // Classes of the project are read from the snapshot; others (FQN only) from the jars of the test classpath.
        var sources = new List<string> { task.CutSource };
        var library = new List<string>();
        var wanted = new HashSet<string>(StringComparer.Ordinal) { m.Cut.Fqn };
        foreach (var name in mentioned)
        {
            if (FindProjectSource(name, task) is { } found)
            {
                sources.Add(found.Source);
                wanted.Add(found.Fqn);
            }
            else if (name.Contains('.', StringComparison.Ordinal))
            {
                library.Add(name);
                wanted.Add(name);
            }
        }

        var text = new StringBuilder();
        try
        {
            using var lease = await javaHelpers.RentAsync(ct).ConfigureAwait(false);
            var api = await lease.Helper.ClassApiAsync(sources, library, task.TestClasspath, ct).ConfigureAwait(false);
            foreach (var type in api.Types.Where(t => wanted.Contains(t.Name)).OrderBy(t => t.Name == m.Cut.Fqn ? 0 : 1))
            {
                text.Append($"Constructors, methods and fields of {type.Name}:\n");
                foreach (var member in type.Members)
                {
                    text.Append("  ").Append(member).Append('\n');
                }

                if (type.Members.Count == 0)
                {
                    text.Append("  (no accessible members)\n");
                }
            }
        }
        catch (Exception ex) when (IsHelperFailure(ex))
        {
            warnings.Add("class_api: " + ex.Message);
        }

        if (errors.Any(e => e.Kind == "package does not exist"))
        {
            var packages = TestPackages(task.TestClasspath);
            text.Append("Packages that exist in the project's test dependencies:\n");
            text.Append(packages.Count == 0 ? "  (none found)\n" : string.Concat(packages.Select(p => $"  {p}\n")));
        }

        return text.ToString();
    }

    /// <summary>
    /// Classes named by the errors: in <c>symbol</c>/<c>location</c>, and in the messages <c>constructor A in class X</c>,
    /// <c>x has private access in X</c>, <c>X is not public in …</c>, <c>X is abstract; cannot be instantiated</c> (R03 §6.5).
    /// The CUT and the generated test are left out; generics are dropped.
    /// </summary>
    public static IReadOnlyList<string> MentionedClasses(IReadOnlyList<CompileError> errors, string cutFqn, string testFqn)
    {
        var names = new List<string>();
        foreach (var error in errors)
        {
            if (error.Location is { } location)
            {
                names.AddRange(TypeMention().Matches(location).Select(x => x.Groups["name"].Value));
            }

            if (error.SymbolKind is "class" or "interface" or "enum" or "record" && error.Symbol is { } symbol)
            {
                names.Add(symbol);
            }

            foreach (var pattern in new[] { ConstructorIn(), PrivateAccessIn(), NotPublic(), AbstractClass() })
            {
                if (pattern.Match(error.Message) is { Success: true } match)
                {
                    names.Add(match.Groups["name"].Value);
                }
            }
        }

        var testSimple = testFqn[(testFqn.LastIndexOf('.') + 1)..];
        return names
            .Select(n => n.Split('<')[0].TrimEnd('.'))
            .Where(n => n.Length > 0 && n != cutFqn && n != testFqn && n != testSimple)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Packages (first 3 parts) with classes in the jars of the test classpath, sorted, at most 60.</summary>
    public static IReadOnlyList<string> TestPackages(IReadOnlyList<string> classpath)
    {
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var jar in classpath.Where(j => j.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && File.Exists(j)))
        {
            try
            {
                using var archive = ZipFile.OpenRead(jar);
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName;
                    var slash = name.LastIndexOf('/');
                    if (slash > 0 && name.EndsWith(".class", StringComparison.Ordinal) && !name.StartsWith("META-INF/", StringComparison.Ordinal))
                    {
                        packages.Add(string.Join('.', name[..slash].Split('/').Take(3)));
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // A broken or locked jar adds no package.
            }
        }

        return packages.Take(MaxPackages).ToList();
    }

    // ── UNKNOWN, DISCOVERY ──────────────────────────────────────────────

    private static string Unknown(VerifyResult verify, RepositoryMetadata m)
    {
        var block = FailureDiagnoser.Limit(verify.BuildErrorExcerpt ?? "(no build error block)", MaxBuildErrorLines);
        var config = MetadataPromptRenderer.Render(m).Split('\n')
            .Where(l => l.StartsWith("- Build tool", StringComparison.Ordinal) || l.StartsWith("- Java version", StringComparison.Ordinal) || l.StartsWith("- Test framework", StringComparison.Ordinal));
        return "Full build error:\n" + block + "\n\nBuild configuration:\n" + string.Join('\n', config) + "\n";
    }

    /// <summary>A test class skeleton for the framework of M, in the package and file of the generated test (P04 §6.5).</summary>
    public static string Discovery(RepositoryMetadata m)
    {
        var fqn = m.Paths.GeneratedTestFqn;
        var className = fqn[(fqn.LastIndexOf('.') + 1)..];
        var package = m.Cut.Package.Length > 0 ? $"package {m.Cut.Package};\n\n" : "";
        var (name, imports, method) = m.TestFramework?.Name switch
        {
            "junit5" => ("JUnit 5", "import static org.junit.jupiter.api.Assertions.assertEquals;\n\nimport org.junit.jupiter.api.Test;", "    @Test\n    void exampleTest() {\n        assertEquals(2, 1 + 1);\n    }"),
            "testng" => ("TestNG", "import static org.testng.Assert.assertEquals;\n\nimport org.testng.annotations.Test;", "    @Test\n    public void exampleTest() {\n        assertEquals(1 + 1, 2);\n    }"),
            _ => ("JUnit 4", "import static org.junit.Assert.assertEquals;\n\nimport org.junit.Test;", "    @Test\n    public void exampleTest() {\n        assertEquals(2, 1 + 1);\n    }"),
        };
        return $"The project runs tests with {name}. A test class that its test runner discovers looks like this (file {m.Paths.GeneratedTestPath}):\n"
               + $"```java\n{package}{imports}\n\npublic class {className} {{\n{method}\n}}\n```\n";
    }

    // ── RUNTIME, ASSERTION ──────────────────────────────────────────────

    private async Task<string> RuntimeAsync(ChainVersion best, TaskContext task, List<string> warnings, CancellationToken ct)
    {
        var m = task.Metadata;
        var scope = new ProjectScope(m.ProjectStructure.Packages, m.Cut.Package, best.Candidate.TestFqn);
        var frames = best.Verify.Failures
            .SelectMany(f => TestResultClassifier.Frames(f.TraceHead))
            .Select(f => (Class: f.Class.Split('$')[0], f.Method))
            .Where(f => scope.IsProjectClass(f.Class))
            .Distinct()
            .ToList();

        var text = new StringBuilder();
        var sources = new List<string>();
        try
        {
            using var lease = await javaHelpers.RentAsync(ct).ConfigureAwait(false);
            foreach (var group in frames.GroupBy(f => f.Class))
            {
                var source = group.Key == m.Cut.Fqn ? task.CutSource : FindProjectSource(group.Key, task)?.Source;
                if (source is null)
                {
                    continue;
                }

                var found = await lease.Helper.MethodSourceAsync(source, group.Select(f => f.Method).Distinct().ToList(), ct).ConfigureAwait(false);
                sources.AddRange(found.Select(x => $"// {group.Key}\n{x.Source}"));
            }
        }
        catch (Exception ex) when (IsHelperFailure(ex))
        {
            warnings.Add("method_source: " + ex.Message);
        }

        if (sources.Count > 0)
        {
            text.Append("Source of the project methods in the stack trace:\n```java\n").Append(string.Join("\n\n", sources)).Append("\n```\n");
        }

        if (await CalledAsync(best, task, warnings, ct).ConfigureAwait(false) is { } called)
        {
            if (called.Constructors.Count > 0)
            {
                text.Append($"Constructors of {m.Cut.Fqn}:\n```java\n").Append(string.Join("\n\n", called.Constructors)).Append("\n```\n");
            }

            if (called.Fields.Count > 0)
            {
                text.Append($"Fields of {m.Cut.Fqn} and how they are initialized:\n").Append(string.Concat(called.Fields.Select(f => $"  {f}\n")));
            }
        }

        if (m.TestFramework?.Mocking.Any(x => x.Contains("mockito", StringComparison.OrdinalIgnoreCase)) == true)
        {
            text.Append("The project has Mockito: mock the dependencies of the CUT that are hard to create or that fail at runtime, instead of constructing them.\n");
        }

        return text.ToString();
    }

    private async Task<string> AssertionAsync(ChainVersion best, TaskContext task, List<string> warnings, CancellationToken ct)
    {
        var text = new StringBuilder("Expected and actual values of the failing assertions:\n");
        foreach (var failure in best.Verify.Failures.Take(FailureDiagnoser.MaxFailures))
        {
            text.Append($"- {(failure.Method.Length > 0 ? failure.Method : "(class)")}: {ExpectedActual(failure)}\n");
        }

        if (await CalledAsync(best, task, warnings, ct).ConfigureAwait(false) is { Methods.Count: > 0 } called)
        {
            text.Append($"Source of the {task.Metadata.Cut.SimpleName} methods called by the failing test methods:\n```java\n")
                .Append(string.Join("\n\n", called.Methods.Select(x => x.Source))).Append("\n```\n");
        }

        return text.ToString();
    }

    /// <summary><c>expected X, actual Y</c> from the message of a failure; otherwise its first line.</summary>
    public static string ExpectedActual(FailureInfo failure)
    {
        var message = failure.Message ?? "";
        var match = ExpectedAngle().Match(message);
        if (!match.Success)
        {
            match = ExpectedPlain().Match(message);
        }

        if (match.Success)
        {
            return $"expected {match.Groups["e"].Value.Trim()}, actual {match.Groups["a"].Value.Trim().Split('\n')[0]}";
        }

        return message.Length == 0 ? TestResultClassifier.ShortName(failure.Type) : message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
    }

    private async Task<CalledMethodsResult?> CalledAsync(ChainVersion best, TaskContext task, List<string> warnings, CancellationToken ct)
    {
        try
        {
            using var lease = await javaHelpers.RentAsync(ct).ConfigureAwait(false);
            return await lease.Helper.CalledMethodsAsync(best.Code, best.Diagnosis.FailingMethods, task.CutSource, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHelperFailure(ex))
        {
            warnings.Add("called_methods: " + ex.Message);
            return null;
        }
    }

    // ── REGRESSION ──────────────────────────────────────────────────────

    private async Task<string> RegressionAsync(ChainVersion best, TaskContext task, List<string> warnings, CancellationToken ct)
    {
        var m = task.Metadata;
        var text = new StringBuilder("Existing tests that fail or stop running when the generated test runs with the module:\n");
        foreach (var regression in best.Verify.Regressions.Take(10))
        {
            var reason = regression.Kind == RegressionKind.Missing
                ? "was not run"
                : $"{regression.Type}{(string.IsNullOrWhiteSpace(regression.Message) ? "" : ": " + FailureDiagnoser.OneLine(regression.Message))}";
            text.Append($"- {FailureDiagnoser.TestName(regression.Test)}: {reason}\n");
        }

        if (await CalledAsync(best, task, warnings, ct).ConfigureAwait(false) is { } called)
        {
            var statics = called.Fields.Where(f => Regex.IsMatch(f, @"\bstatic\b") && !Regex.IsMatch(f, @"\bstatic\s+final\b|\bfinal\s+static\b")).ToList();
            text.Append($"Static fields of {m.Cut.Fqn} (shared by every test in the JVM):\n");
            text.Append(statics.Count == 0 ? "  (none)\n" : string.Concat(statics.Select(s => $"  {s}\n")));
        }

        var lines = best.Code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim())
            .Where(l => !l.StartsWith("import", StringComparison.Ordinal))
            .Where(l => SharedState().IsMatch(l) || l.Contains(m.Cut.SimpleName + ".set", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        text.Append("Code of the generated test that changes shared state (system properties, default locale or time zone, files, static members):\n");
        text.Append(lines.Count == 0 ? "  (none found)\n" : string.Concat(lines.Select(l => $"  {l}\n")));
        return text.ToString();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static bool IsHelperFailure(Exception ex) => ex is ArrowException or JavaHelperCommandException or IOException or TimeoutException;

    /// <summary>
    /// The source of a project class in the compiled snapshot: by FQN, or a simple name looked up in the CUT package and the
    /// packages of M; a nested class gives the file of its outer class. Null when not found.
    /// </summary>
    internal static (string Fqn, string Source)? FindProjectSource(string name, TaskContext task)
    {
        var m = task.Metadata;
        var snapshot = Path.Combine(task.Run.HomeRoot, task.Workspace.CompiledSnapshot.Replace('/', Path.DirectorySeparatorChar));
        var outer = name.Split('$')[0];
        var candidates = outer.Contains('.', StringComparison.Ordinal)
            ? [outer]
            : new[] { m.Cut.Package }.Concat(m.ProjectStructure.Packages).Distinct(StringComparer.Ordinal)
                .Select(p => p.Length == 0 ? outer : $"{p}.{outer}").ToList();
        foreach (var fqn in candidates)
        {
            foreach (var root in new[] { m.Paths.SourceRoot, m.Paths.TestRoot }.Where(r => r.Length > 0))
            {
                var path = Path.Combine(snapshot, root.Replace('/', Path.DirectorySeparatorChar), fqn.Replace('.', Path.DirectorySeparatorChar) + ".java");
                if (File.Exists(path))
                {
                    return (fqn, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
                }
            }
        }

        return null;
    }
}
