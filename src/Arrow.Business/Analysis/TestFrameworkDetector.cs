using System.Text.RegularExpressions;
using Arrow.Common.Analysis;

namespace Arrow.Business.Analysis;

/// <summary>Detects the test framework, mocking and assertion libraries of a module (P01 §4.5).</summary>
public static partial class TestFrameworkDetector
{
    /// <summary><c>missing_reasons.test_framework</c> when no framework dependency is found.</summary>
    public const string NoFrameworkReason = "no test framework dependency";

    [GeneratedRegex(@"^\s*import\s+(static\s+)?org\.testng\.", RegexOptions.Multiline)]
    private static partial Regex TestNgImport();

    [GeneratedRegex(@"^\s*import\s+(static\s+)?org\.junit\.(?!jupiter\.)", RegexOptions.Multiline)]
    private static partial Regex JUnit4Import();

    [GeneratedRegex(@"^\s*import\s+(static\s+)?org\.junit\.jupiter\.", RegexOptions.Multiline)]
    private static partial Regex JupiterImport();

    /// <param name="dependencies">Resolved dependencies of the test classpath, transitive ones included.</param>
    /// <param name="testSources">Sources of the module's existing tests; used only when several frameworks are on the classpath.</param>
    /// <param name="direct">Declared dependencies: <c>hamcrest-core</c> counts as an assertion library only when declared, because JUnit 4 always brings it.</param>
    public static TestFrameworkMetadata Detect(
        IReadOnlyList<ResolvedDependency> dependencies,
        IEnumerable<string>? testSources = null,
        IReadOnlyList<DependencyRef>? direct = null)
    {
        ResolvedDependency? Find(Func<ResolvedDependency, bool> match) => dependencies.FirstOrDefault(match);

        var jupiter = Find(d => d.Group == "org.junit.jupiter" && d.Artifact is "junit-jupiter-api" or "junit-jupiter" or "junit-jupiter-engine")
                      ?? Find(d => d.Group == "org.junit.jupiter");
        var vintage = Find(d => d.Group == "org.junit.vintage" && d.Artifact == "junit-vintage-engine");
        var junit4 = Find(d => d.Group == "junit" && d.Artifact == "junit");
        var testng = Find(d => d.Group == "org.testng" && d.Artifact == "testng");

        var found = new List<(string Name, ResolvedDependency Dependency)>();
        if (jupiter is not null)
        {
            found.Add(("junit5", jupiter));
        }

        if (junit4 is not null)
        {
            found.Add(("junit4", junit4));
        }

        if (testng is not null)
        {
            found.Add(("testng", testng));
        }

        var mocking = dependencies
            .Where(d => d.Artifact.StartsWith("mockito-", StringComparison.Ordinal) || d.Artifact is "easymock" or "jmockit" || d.Artifact.StartsWith("powermock-", StringComparison.Ordinal))
            .Select(d => d.Coordinates).Distinct().ToList();
        var assertions = dependencies
            .Where(d => d.Artifact is "assertj-core" or "truth" || d.Artifact.StartsWith("hamcrest", StringComparison.Ordinal))
            .Where(d => !(d.Artifact == "hamcrest-core" && junit4 is not null && direct?.Any(x => x.Artifact == "hamcrest-core") != true))
            .Select(d => d.Coordinates).Distinct().ToList();

        if (found.Count == 0)
        {
            return new TestFrameworkMetadata(null, "", false, mocking, assertions, NoFrameworkReason);
        }

        var (name, dependency) = found[0];
        var evidence = Evidence(dependency);
        if (found.Count > 1)
        {
            // Several frameworks: the one most imported by the existing tests; without tests, junit5, then junit4, then testng.
            var counts = CountImports(testSources ?? []);
            var best = found.OrderByDescending(f => counts[f.Name]).First();
            if (counts[best.Name] > 0)
            {
                (name, dependency) = best;
                evidence = $"{Evidence(dependency)}; imports {counts["junit5"]} org.junit.jupiter, {counts["junit4"]} org.junit, {counts["testng"]} org.testng";
            }
        }

        return new TestFrameworkMetadata(name, dependency.Version, name == "junit5" && vintage is not null, mocking, assertions, evidence);
    }

    /// <summary>Files importing each framework: <c>junit5</c>, <c>junit4</c>, <c>testng</c>.</summary>
    public static IReadOnlyDictionary<string, int> CountImports(IEnumerable<string> sources)
    {
        var counts = new Dictionary<string, int> { ["junit5"] = 0, ["junit4"] = 0, ["testng"] = 0 };
        foreach (var source in sources)
        {
            counts["junit5"] += JupiterImport().IsMatch(source) ? 1 : 0;
            counts["junit4"] += JUnit4Import().IsMatch(source) ? 1 : 0;
            counts["testng"] += TestNgImport().IsMatch(source) ? 1 : 0;
        }

        return counts;
    }

    private static string Evidence(ResolvedDependency d) => $"dependency {d.Group}:{d.Artifact} ({d.Scope})";
}
