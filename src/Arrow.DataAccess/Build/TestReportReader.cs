using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Arrow.DataAccess.Build;

/// <summary>Result of one test method in a report.</summary>
public enum TestCaseStatus
{
    Passed,
    Failed,
    Error,
    Skipped,
}

/// <summary>A <c>&lt;testcase&gt;</c> of a JUnit XML report.</summary>
/// <param name="ClassName">Test class, nested classes written with <c>$</c>.</param>
/// <param name="Name">Method name, without a trailing <c>()</c>.</param>
/// <param name="Status">Result.</param>
/// <param name="Message">Message of the failure or error.</param>
/// <param name="ExceptionType">Exception class of the failure or error.</param>
/// <param name="Trace">Stack trace text.</param>
/// <param name="TimeSeconds">Time reported by the runner.</param>
public sealed record TestCaseResult(
    string ClassName,
    string Name,
    TestCaseStatus Status,
    string? Message = null,
    string? ExceptionType = null,
    string? Trace = null,
    double TimeSeconds = 0)
{
    /// <summary><c>&lt;class&gt;#&lt;method&gt;</c>, the id used in <c>baseline.json</c>.</summary>
    public string Id => ClassName + "#" + Name;

    /// <summary>Top-level class: a nested test class belongs to its outer class (P01 §6.2).</summary>
    public string OuterClassName => ClassName.Split('$')[0];
}

/// <summary>
/// Reads <c>TEST-*.xml</c> under <c>target/surefire-reports</c> (Maven) and <c>build/test-results/&lt;task&gt;</c> (Gradle)
/// of every module below a folder (P01 §6.2). Shared with block 3. A broken file (process killed while writing) is skipped.
/// </summary>
public static partial class TestReportReader
{
    // from ARROW_Framework/src/Arrow.Service/External/TestReportReader.cs
    [GeneratedRegex(@"^[\p{L}_$][\p{L}\p{N}_$]*(\.[\p{L}_$][\p{L}\p{N}_$]*)*$")]
    private static partial Regex QualifiedName();

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Every report below <paramref name="directory"/>, in file name order.</summary>
    public static IReadOnlyList<TestCaseResult> Read(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "TEST-*.xml", Options)
            .Where(IsReport)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(ReadFile)
            .ToList();
    }

    /// <summary>
    /// Only files directly in <c>surefire-reports/</c> or in <c>test-results/&lt;task&gt;/</c>. TestNG also writes copies into
    /// <c>surefire-reports/junitreports/</c>; reading both would count each test twice.
    /// </summary>
    public static bool IsReport(string file)
    {
        var parent = Path.GetDirectoryName(file);
        var grandParent = parent is null ? null : Path.GetDirectoryName(parent);
        return Path.GetFileName(parent) == "surefire-reports" || (grandParent is not null && Path.GetFileName(grandParent) == "test-results");
    }

    /// <summary>Reads one report; a file that is not XML gives no test.</summary>
    public static IReadOnlyList<TestCaseResult> ReadFile(string file)
    {
        try
        {
            return Parse(XDocument.Load(file));
        }
        catch (XmlException)
        {
            return [];
        }
    }

    /// <summary>
    /// Class of a test case. Surefire 3 with JUnit 5 may write a method name or display name (<c>testInsert()</c>) as
    /// <c>classname</c>, so <c>classname</c> is used only when it is a valid Java class name, and has a package when the
    /// suite name has one; otherwise the suite name, which is always the test class.
    /// </summary>
    public static string ClassNameOf(string? testCaseClass, string suiteName)
    {
        if (string.IsNullOrEmpty(testCaseClass) || !QualifiedName().IsMatch(testCaseClass))
        {
            return suiteName;
        }

        return suiteName.Contains('.', StringComparison.Ordinal) && !testCaseClass.Contains('.', StringComparison.Ordinal) ? suiteName : testCaseClass;
    }

    /// <summary>Method name without a trailing <c>()</c> (Gradle with JUnit 5 writes <c>adds()</c>).</summary>
    public static string MethodName(string? name) =>
        name is null ? "" : name.EndsWith("()", StringComparison.Ordinal) ? name[..^2] : name;

    /// <summary>Test cases of every <c>&lt;testsuite&gt;</c> in the document.</summary>
    public static IReadOnlyList<TestCaseResult> Parse(XDocument document)
    {
        var results = new List<TestCaseResult>();
        foreach (var suite in document.Descendants().Where(e => e.Name.LocalName == "testsuite"))
        {
            var suiteName = (string?)suite.Attribute("name") ?? "";
            foreach (var testCase in suite.Elements().Where(e => e.Name.LocalName == "testcase"))
            {
                var failure = testCase.Elements().FirstOrDefault(e => e.Name.LocalName == "failure");
                var error = testCase.Elements().FirstOrDefault(e => e.Name.LocalName == "error");
                var skipped = testCase.Elements().Any(e => e.Name.LocalName == "skipped");
                var problem = error ?? failure;
                results.Add(new TestCaseResult(
                    ClassNameOf((string?)testCase.Attribute("classname"), suiteName),
                    MethodName((string?)testCase.Attribute("name")),
                    error is not null ? TestCaseStatus.Error : failure is not null ? TestCaseStatus.Failed : skipped ? TestCaseStatus.Skipped : TestCaseStatus.Passed,
                    problem is null ? null : (string?)problem.Attribute("message"),
                    problem is null ? null : (string?)problem.Attribute("type"),
                    problem is null || problem.Value.Trim().Length == 0 ? null : problem.Value.Trim(),
                    double.TryParse((string?)testCase.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0));
            }
        }

        return results;
    }
}
