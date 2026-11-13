using System.Xml.Linq;
using Arrow.DataAccess.Processes;

namespace Arrow.IntegrationTests.Fixtures;

/// <summary>
/// The four fixtures added in GĐ0 (P11 §2): each builds, has the baseline its README states, and its hand-written
/// sample suite in <c>.arrow-sample/</c> passes. Needs Maven and the JDK of the fixture (<c>JAVA_HOME_&lt;n&gt;_X64</c>).
/// </summary>
[Trait("Category", "Integration")]
public class NewFixtureTests
{
    public static TheoryData<string, int, int, string> Fixtures => new()
    {
        // fixture, JDK, tests in the baseline, path of the generated test
        { "maven-no-tests", 8, 0, "src/test/java/com/acme/text/Slugifier_ArrowTest.java" },
        { "maven-testng", 11, 2, "src/test/java/com/acme/inventory/Stock_ArrowTest.java" },
        { "maven-wrapper-junit5", 11, 1, "src/test/java/com/acme/geo/Distance_ArrowTest.java" },
        { "maven-existing-arrowtest", 8, 1, "src/test/java/com/acme/text/WordCounter_Arrow2Test.java" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task BaselineAndSampleSuiteBehaveAsTheReadmeSays(string fixture, int jdk, int baselineTests, string generatedTestPath)
    {
        var javaHome = Environment.GetEnvironmentVariable($"JAVA_HOME_{jdk}_X64");
        Assert.SkipWhen(string.IsNullOrEmpty(javaHome) || !Directory.Exists(javaHome), $"JAVA_HOME_{jdk}_X64 is not set");
        var before = FixtureRepo.FingerprintOf(Path.Combine(FixtureRepo.FixturesDir, fixture));

        using var repo = new FixtureRepo(fixture);
        var baseline = await MavenAsync(repo, javaHome!, "test");
        Assert.True(baseline.Succeeded, Describe(baseline));
        Assert.Equal((baselineTests, 0), Results(repo.Root));

        var sample = Directory.GetFiles(Path.Combine(repo.Root, ".arrow-sample"), "*.java").Single();
        var target = Path.Combine(repo.Root, generatedTestPath);
        Assert.Equal(Path.GetFileName(target), Path.GetFileName(sample));
        Assert.False(File.Exists(target), "the generated test must not exist yet");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sample, target);

        // "clean": Surefire leaves the reports of the previous run in place (with TestNG, a per-class file next to
        // the new TEST-TestSuite.xml), which would be counted twice.
        var withSample = await MavenAsync(repo, javaHome!, "clean", "test");
        Assert.True(withSample.Succeeded, Describe(withSample));
        Assert.Equal((baselineTests + 5, 0), Results(repo.Root));

        Assert.Equal(before, FixtureRepo.FingerprintOf(repo.Source));
    }

    [Fact]
    public async Task WrapperFixtureRunsItsOwnMavenVersion()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME_11_X64");
        Assert.SkipWhen(string.IsNullOrEmpty(javaHome), "JAVA_HOME_11_X64 is not set");
        using var repo = new FixtureRepo("maven-wrapper-junit5");
        var version = await MavenAsync(repo, javaHome!, "-v");
        Assert.True(version.Succeeded, Describe(version));
        Assert.Contains("Apache Maven 3.9.9", version.Output, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> MavenAsync(FixtureRepo repo, string javaHome, params string[] goals)
    {
        var wrapper = File.Exists(Path.Combine(repo.Root, "mvnw"));
        var (program, prefix) = (wrapper, OperatingSystem.IsWindows()) switch
        {
            (true, true) => (Path.Combine(repo.Root, "mvnw.cmd"), Array.Empty<string>()),
            (true, false) => ("/bin/sh", new[] { Path.Combine(repo.Root, "mvnw") }),
            (false, true) => (OnPath("mvn.cmd"), Array.Empty<string>()),
            _ => (OnPath("mvn"), Array.Empty<string>()),
        };
        string[] args = goals is ["-v"] ? [.. prefix, "-v"] : [.. prefix, "-B", "-ntp", .. goals];
        var spec = new ProcessSpec(program, args, repo.Root,
            Environment: new Dictionary<string, string?> { ["JAVA_HOME"] = javaHome, ["MAVEN_ARGS"] = null },
            Timeout: TimeSpan.FromMinutes(10));
        return await new ProcessRunner().RunAsync(spec, TestContext.Current.CancellationToken);
    }

    /// <summary>Absolute path of a program on PATH, as block 1 will find it (P01 §4.2).</summary>
    private static string OnPath(string program)
    {
        var found = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), program))
            .FirstOrDefault(File.Exists);
        Assert.SkipWhen(found is null, program + " is not on PATH");
        return found!;
    }

    private static string Describe(ProcessResult result) =>
        "exit=" + (result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "killed") + "\n" + result.Tail(40);

    /// <summary>Tests run and tests failed or in error, from the Surefire reports.</summary>
    private static (int Tests, int Failed) Results(string root)
    {
        var reports = Path.Combine(root, "target", "surefire-reports");
        if (!Directory.Exists(reports))
        {
            return (0, 0);
        }

        var suites = Directory.GetFiles(reports, "TEST-*.xml").Select(f => XDocument.Load(f).Root!).ToList();
        int Sum(string attribute) => suites.Sum(s => int.Parse((string?)s.Attribute(attribute) ?? "0", System.Globalization.CultureInfo.InvariantCulture));
        return (Sum("tests"), Sum("failures") + Sum("errors"));
    }
}
