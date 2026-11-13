using System.Text.RegularExpressions;
using Arrow.Common;
using Arrow.Common.Verification;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Repair;

/// <summary>The classes that count as project code when looking for the first project frame of a stack trace (P04 §4.1).</summary>
/// <param name="Packages"><c>project_structure.packages</c> of M.</param>
/// <param name="CutPackage">Package of the CUT; its sub-packages count too.</param>
/// <param name="GeneratedTestFqn">The generated test class, which never counts.</param>
public sealed record ProjectScope(IReadOnlyList<string> Packages, string CutPackage, string GeneratedTestFqn)
{
    /// <summary>The class is in one of the packages or under the CUT package, and is not the generated test (nor one of its nested classes).</summary>
    public bool IsProjectClass(string className)
    {
        var outer = className.Split('$')[0];
        if (outer == GeneratedTestFqn)
        {
            return false;
        }

        var dot = outer.LastIndexOf('.');
        var package = dot < 0 ? "" : outer[..dot];
        return Packages.Contains(package, StringComparer.Ordinal)
               || package == CutPackage
               || (CutPackage.Length > 0 && package.StartsWith(CutPackage + ".", StringComparison.Ordinal));
    }
}

/// <summary>Result of running the generated class alone (step 2–4).</summary>
/// <param name="FailedState">DISCOVERY, RUNTIME or ASSERTION; null when every method passed.</param>
/// <param name="Counts">Test methods run, passed and failed.</param>
/// <param name="Failures">Failing methods, failures of the whole class first.</param>
/// <param name="FailingMethods">Names of the failing methods.</param>
public sealed record TargetRunResult(VerificationState? FailedState, TestMethodCounts Counts, IReadOnlyList<FailureInfo> Failures, IReadOnlyList<string> FailingMethods);

/// <summary>Classifies the run of the generated class (P04 §3.4).</summary>
public static partial class TestResultClassifier
{
    /// <summary>Lines of stack trace kept per failure.</summary>
    public const int TraceHeadLines = 8;

    // from ARROW_Framework/src/Arrow.Service/Block3/TestResultClassifier.cs
    private static readonly HashSet<string> AssertionTypes = new(StringComparer.Ordinal)
    {
        "java.lang.AssertionError", "org.opentest4j.AssertionFailedError", "org.opentest4j.MultipleFailuresError",
        "org.junit.ComparisonFailure", "org.junit.internal.ArrayComparisonFailure", "junit.framework.AssertionFailedError",
        "junit.framework.ComparisonFailure",
    };

    private static readonly string[] AssertionPrefixes = ["org.assertj.core.error.", "org.mockito.exceptions.verification.", "org.hamcrest."];

    /// <summary>Method names of a failure of the whole class (initialization, <c>@BeforeAll</c>…).</summary>
    private static readonly HashSet<string> ClassLevelNames = new(StringComparer.Ordinal) { "initializationError", "classMethod", "", "executionError" };

    [GeneratedRegex(@"^[\p{L}_$][\p{L}\p{N}_$]*(\.[\p{L}_$][\p{L}\p{N}_$]*)+$")]
    private static partial Regex QualifiedName();

    // from ARROW_Framework/src/Arrow.Service/Block3/TestResultClassifier.cs
    [GeneratedRegex(@"^\s*(?:Caused by:\s*)?(?<name>[\p{L}_$][\p{L}\p{N}_$]*(?:\.[\p{L}_$][\p{L}\p{N}_$]*)+)(?::|\s*$)")]
    private static partial Regex ExceptionLine();

    // from ARROW_Framework/src/Arrow.Service/Block3/TestResultClassifier.cs
    [GeneratedRegex(@"^\s*at\s+(?:[\w.\-]+/+)?(?<cls>[\p{L}_$][\p{L}\p{N}_$.]*)\.(?<method>[\p{L}_$<][\p{L}\p{N}_$>]*)\(")]
    private static partial Regex FrameLine();

    /// <summary>The test belongs to <paramref name="testFqn"/>, nested classes (<c>Outer$Inner</c>) included.</summary>
    public static bool Belongs(TestCaseResult test, string testFqn) =>
        test.ClassName == testFqn || test.ClassName.StartsWith(testFqn + "$", StringComparison.Ordinal);

    /// <summary>
    /// Step 2–4: no method ran (or JUnit 4 "No runnable methods") → DISCOVERY; a failure that is not an assertion, or a failure
    /// of the whole class → RUNTIME; only assertion failures → ASSERTION; no failure → null.
    /// </summary>
    /// <param name="allTests">Every test case of the reports.</param>
    /// <param name="testFqn">The generated class.</param>
    /// <param name="scope">Project classes, for the frame of each failure.</param>
    public static TargetRunResult Classify(IReadOnlyList<TestCaseResult> allTests, string testFqn, ProjectScope scope)
    {
        var tests = allTests.Where(t => Belongs(t, testFqn)).ToList();
        var classLevel = tests.Where(t => ClassLevelNames.Contains(t.Name) && t.Status is TestCaseStatus.Failed or TestCaseStatus.Error).ToList();
        if (classLevel.Any(t => (t.Message ?? "").Contains("No runnable methods", StringComparison.Ordinal)))
        {
            return new TargetRunResult(VerificationState.Discovery, TestMethodCounts.None, [], []);
        }

        var methods = tests.Where(t => !ClassLevelNames.Contains(t.Name) && t.Status != TestCaseStatus.Skipped).ToList();
        if (methods.Count == 0 && classLevel.Count == 0)
        {
            return new TargetRunResult(VerificationState.Discovery, TestMethodCounts.None, [], []);
        }

        var failed = classLevel.Concat(methods.Where(t => t.Status is TestCaseStatus.Failed or TestCaseStatus.Error)).ToList();
        var failures = failed.Select(t => ToFailure(t, scope)).ToList();
        var counts = new TestMethodCounts(
            methods.Count,
            methods.Count(t => t.Status == TestCaseStatus.Passed),
            methods.Count(t => t.Status != TestCaseStatus.Passed));
        var failingMethods = failed.Select(t => t.Name).Where(n => n.Length > 0 && !ClassLevelNames.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
        VerificationState? state = failures.Count == 0
            ? null
            : classLevel.Count > 0 || failures.Any(f => !IsAssertion(f.Type, f.Message)) ? VerificationState.Runtime : VerificationState.Assertion;
        return new TargetRunResult(state, counts, failures, failingMethods);
    }

    /// <summary>A failed test case as a <see cref="FailureInfo"/>, with the first 8 trace lines and the project frame.</summary>
    public static FailureInfo ToFailure(TestCaseResult test, ProjectScope scope) => new(
        test.Name,
        ExceptionName(test.ExceptionType, test.Trace),
        test.Message,
        test.Trace is null ? null : string.Join('\n', SplitLines(test.Trace).Take(TraceHeadLines)),
        ProjectFrame(test.Trace, scope));

    /// <summary>
    /// The exception name: the <c>type</c> attribute when it is a valid class name; otherwise (some plugins write the message
    /// there) the exception of the first trace line or of the first <c>Caused by:</c> line (P04 §3.4 step 2).
    /// </summary>
    public static string ExceptionName(string? type, string? trace)
    {
        if (type is not null && QualifiedName().IsMatch(type.Trim()))
        {
            return type.Trim();
        }

        var lines = SplitLines(trace ?? "");
        foreach (var line in lines.Take(1).Concat(lines.Where(l => l.TrimStart().StartsWith("Caused by:", StringComparison.Ordinal)).Take(1)))
        {
            if (ExceptionLine().Match(line) is { Success: true } match)
            {
                return match.Groups["name"].Value;
            }
        }

        return string.IsNullOrWhiteSpace(type) ? "unknown" : type.Trim();
    }

    /// <summary>
    /// An assertion failure (P04 §3.4 step 3): the assertion classes of JUnit, opentest4j, AssertJ, Mockito verification and
    /// Hamcrest, and the failure of an expected exception that was not thrown (<c>assertThrows</c>, <c>@Test(expected)</c>,
    /// TestNG <c>expectedExceptions</c>).
    /// </summary>
    public static bool IsAssertion(string? exception, string? message)
    {
        if (exception is null)
        {
            return false;
        }

        if (AssertionTypes.Contains(exception) || AssertionPrefixes.Any(p => exception.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }

        if (message is null)
        {
            return false;
        }

        return (exception == "org.testng.TestException"
                && (message.Contains("should have thrown", StringComparison.OrdinalIgnoreCase) || message.Contains("Expected exception", StringComparison.OrdinalIgnoreCase)))
               || (message.StartsWith("Expected ", StringComparison.Ordinal) && message.Contains(" to be thrown", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>Class.method</c> of the first frame whose class is project code (<paramref name="scope"/>); without one, of the first
    /// frame; null when the trace has no frame (P04 §4.1).
    /// </summary>
    public static string? ProjectFrame(string? trace, ProjectScope scope)
    {
        string? first = null;
        foreach (var line in SplitLines(trace ?? ""))
        {
            if (FrameLine().Match(line) is not { Success: true } match)
            {
                continue;
            }

            var cls = match.Groups["cls"].Value;
            var frame = $"{ShortName(cls)}.{match.Groups["method"].Value}";
            if (scope.IsProjectClass(cls))
            {
                return frame;
            }

            first ??= frame;
        }

        return first;
    }

    /// <summary>The classes of the frames of <paramref name="trace"/>, in order, without repeats.</summary>
    public static IReadOnlyList<(string Class, string Method)> Frames(string? trace) =>
        SplitLines(trace ?? "")
            .Select(l => FrameLine().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups["cls"].Value, m.Groups["method"].Value))
            .Distinct()
            .ToList();

    /// <summary><c>org.opentest4j.AssertionFailedError</c> → <c>AssertionFailedError</c>; nested classes keep <c>$</c>.</summary>
    public static string ShortName(string name) => name[(name.LastIndexOf('.') + 1)..];

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}

/// <summary>Result of step 5 (P04 §3.5).</summary>
/// <param name="Regressions">The regressions; empty when the step passed.</param>
/// <param name="GeneratedTestRan">The generated test ran with the module; false means the Surefire includes may not match its name.</param>
public sealed record RegressionCheck(IReadOnlyList<RegressionInfo> Regressions, bool GeneratedTestRan);

/// <summary>Module regression of step 5 (P04 §3.5).</summary>
public static class RegressionChecker
{
    /// <summary>
    /// (a) a test of <c>passing_in_all_runs</c> fails, (b) it is missing from the report, (c) the generated test fails with the
    /// module. Flaky tests and tests failing at baseline are not in <paramref name="baselinePassing"/>, so they never count; a
    /// generated test that did not run is not a regression.
    /// </summary>
    /// <param name="baselinePassing"><c>baseline.passing_in_all_runs</c>.</param>
    /// <param name="moduleRun">Test cases of step 5.</param>
    /// <param name="testFqn">The generated class.</param>
    public static RegressionCheck Check(IReadOnlyList<string> baselinePassing, IReadOnlyList<TestCaseResult> moduleRun, string testFqn)
    {
        // A test reported twice (TestNG suite and class report, re-runs) counts once, with its worst status.
        var byId = moduleRun.GroupBy(t => t.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => Severity(t.Status)).First(), StringComparer.Ordinal);
        var regressions = new List<RegressionInfo>();
        foreach (var id in baselinePassing)
        {
            if (!byId.TryGetValue(id, out var test))
            {
                regressions.Add(new RegressionInfo(id, RegressionKind.Missing));
            }
            else if (test.Status is TestCaseStatus.Failed or TestCaseStatus.Error)
            {
                regressions.Add(new RegressionInfo(id, RegressionKind.Failed, TestResultClassifier.ExceptionName(test.ExceptionType, test.Trace), test.Message));
            }
        }

        var generated = byId.Values.Where(t => TestResultClassifier.Belongs(t, testFqn)).ToList();
        regressions.AddRange(generated
            .Where(t => t.Status is TestCaseStatus.Failed or TestCaseStatus.Error)
            .Select(t => new RegressionInfo(t.Id, RegressionKind.GeneratedFailedInModule, TestResultClassifier.ExceptionName(t.ExceptionType, t.Trace), t.Message)));
        return new RegressionCheck(regressions, generated.Any(t => t.Status != TestCaseStatus.Skipped));
    }

    private static int Severity(TestCaseStatus status) => status switch
    {
        TestCaseStatus.Error => 3,
        TestCaseStatus.Failed => 2,
        TestCaseStatus.Passed => 1,
        _ => 0,
    };
}
