using Arrow.Business.UseCases;
using Arrow.Common.Errors;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.Tests.UseCases;

/// <summary><c>arrow doctor</c> (P08 §2.6) with fake JDKs, tools and disk.</summary>
public class DoctorUseCaseTests
{
    private sealed class FakeProbe(string? version = "1.1.0", string? failWith = null) : IJavaHelperProbe
    {
        public string? Java { get; private set; }

        public Task<string> PingAsync(string javaExecutable, CancellationToken ct)
        {
            Java = javaExecutable;
            return failWith is null ? Task.FromResult(version!) : throw new ArrowException(failWith);
        }
    }

    private static DoctorUseCase Doctor(FakeMachine machine, IJavaHelperProbe? probe = null, Func<string, long?>? free = null, Func<bool?>? longPaths = null) =>
        new(machine.Config, machine.Home, machine.Discovery, machine.Runner, probe ?? new FakeProbe(), free ?? (_ => 50L * 1024 * 1024 * 1024), longPaths ?? (() => null));

    private static FakeMachine Complete()
    {
        var machine = new FakeMachine();
        machine.AddJdk(8, "1.8.0_422");
        machine.AddJdk(11, "11.0.24");
        machine.AddJdk(17, "17.0.12");
        machine.AddJdk(21, "21.0.4");
        machine.AddTool("git", "git version 2.46.0");
        return machine;
    }

    [Fact]
    public async Task CompleteMachineIsReady()
    {
        using var machine = Complete();
        machine.AddTool("mvn", "Apache Maven 3.9.9 (abc)");
        machine.AddTool("gradle", "\n------\nGradle 8.10\n------");
        var seen = new List<DoctorItem>();

        var report = await Doctor(machine).RunAsync(null, seen.Add, TestContext.Current.CancellationToken);

        Assert.True(report.Ready);
        Assert.Equal((0, 0), (report.Warnings, report.Errors));
        Assert.Equal(seen, report.Items);
        Assert.Equal(["ARROW_HOME", "Cấu hình", "git", "JDK 8 / 11 / 17 / 21", "Maven", "Gradle", "java-helper"], report.Items.Select(i => i.Name));
        Assert.Equal("1.8.0_422 / 11.0.24 / 17.0.12 / 21.0.4", report.Items.Single(i => i.Name.StartsWith("JDK", StringComparison.Ordinal)).Detail);
        Assert.Equal("3.9.9", report.Items.Single(i => i.Name == "Maven").Detail);
        Assert.Equal("git", report.Items[2].Name);
        Assert.Equal("2.46.0", report.Items[2].Detail);
        Assert.Contains("50 GB trống", report.Items[0].Detail, StringComparison.Ordinal);
        Assert.Contains("(JDK 17)", report.Items[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingJdkIsAWarningAndNoJdkAtAllIsAnError()
    {
        using var machine = new FakeMachine();
        machine.AddJdk(11, "11.0.24");
        machine.AddJdk(17, "17.0.12");
        var report = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);
        Assert.True(report.Ready);
        var jdk8 = Assert.Single(report.Items, i => i.Name == "JDK 8");
        Assert.Equal(DoctorLevel.Warning, jdk8.Level);
        Assert.Contains("chỉ cần cho project Java 8", jdk8.Detail, StringComparison.Ordinal);
        Assert.Contains("— / 11.0.24 / 17.0.12 / —", report.Items.Single(i => i.Name == "JDK 8 / 11 / 17 / 21").Detail, StringComparison.Ordinal);

        using var empty = new FakeMachine();
        var none = await Doctor(empty).RunAsync(null, null, TestContext.Current.CancellationToken);
        Assert.False(none.Ready);
        var error = Assert.Single(none.Items, i => i.Level == DoctorLevel.Error && i.Name == "JDK");
        Assert.Contains("JDK 17", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyOldJdksCannotRunJavaHelper()
    {
        using var machine = new FakeMachine();
        machine.AddJdk(8, "1.8.0_422");
        var report = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);
        Assert.False(report.Ready);
        var item = Assert.Single(report.Items, i => i.Level == DoctorLevel.Error);
        Assert.Equal("JDK cho java-helper", item.Name);
        Assert.DoesNotContain(report.Items, i => i.Name == "java-helper");
    }

    [Fact]
    public async Task JreAndBrokenConfiguredHomesAreReported()
    {
        using var machine = new FakeMachine();
        machine.AddJdk(17, "17.0.12");
        machine.AddJdk(11, "11.0.24", withJavac: false);
        var missing = Path.Combine(machine.Root, "nowhere", "jdk-8");
        File.WriteAllText(machine.Home.ConfigFile, "{ \"schema_version\": 1, \"java\": { \"jdk_homes\": { \"8\": " + System.Text.Json.JsonSerializer.Serialize(missing) + " } } }");

        var report = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);

        var broken = Assert.Single(report.Items, i => i.Name == "java.jdk_homes");
        Assert.Equal(DoctorLevel.Warning, broken.Level);
        Assert.Contains(missing, broken.Detail, StringComparison.Ordinal);
        // The JRE of "11" has no javac, so it is not a JDK.
        Assert.Contains("— / — / 17.0.12 / —", report.Items.Single(i => i.Name.StartsWith("JDK 8 /", StringComparison.Ordinal)).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitAndBuildToolsAreOptional()
    {
        using var machine = new FakeMachine();
        machine.AddJdk(17, "17.0.12");
        var report = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);

        Assert.True(report.Ready);
        var git = Assert.Single(report.Items, i => i.Name == "git");
        Assert.Equal(DoctorLevel.Warning, git.Level);
        Assert.Contains("--repo", git.Detail, StringComparison.Ordinal);
        var tools = Assert.Single(report.Items, i => i.Name == "Maven / Gradle");
        Assert.Equal(DoctorLevel.Warning, tools.Level);
        Assert.Contains("wrapper", tools.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyGradleIsEnough()
    {
        using var machine = new FakeMachine();
        machine.AddJdk(17, "17.0.12");
        machine.AddTool("gradle", "Gradle 8.14.3");
        var report = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(report.Items, i => i.Name == "Maven / Gradle");
        Assert.Equal("8.14.3", report.Items.Single(i => i.Name == "Gradle").Detail);
    }

    [Fact]
    public async Task ABuildToolThatDoesNotRunIsAWarningWithTheFirstError()
    {
        using var machine = new FakeMachine();
        machine.AddJdk(17, "17.0.12");
        machine.AddTool("mvn", "FAIL: The JAVA_HOME environment variable is not defined correctly");
        var report = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);
        var maven = Assert.Single(report.Items, i => i.Name == "Maven");
        Assert.Equal(DoctorLevel.Warning, maven.Level);
        Assert.Contains("JAVA_HOME", maven.Detail, StringComparison.Ordinal);
        // The build tool runs with the JDK 17 of the machine.
        Assert.Equal(machine.Environment["JAVA_HOME_17_X64"], machine.Runner.Commands.Single(c => c.FileName.EndsWith("mvn", StringComparison.Ordinal)).Environment!["JAVA_HOME"]);
    }

    [Fact]
    public async Task HomeProblemsAreErrors()
    {
        using var machine = Complete();
        var lowSpace = await Doctor(machine, free: _ => 500L * 1024 * 1024).RunAsync(null, null, TestContext.Current.CancellationToken);
        var disk = Assert.Single(lowSpace.Items, i => i.Level == DoctorLevel.Error);
        Assert.Equal("ARROW_HOME", disk.Name);
        Assert.Contains("chỉ còn", disk.Detail, StringComparison.Ordinal);
        Assert.False(lowSpace.Ready);

        // A path with spaces (P09 §2): CFG_HOME_PATH.
        var spaced = new DataAccess.Storage.ArrowHome(Path.Combine(machine.Root, "my home"));
        var bad = await new DoctorUseCase(machine.Config, spaced, machine.Discovery, machine.Runner, new FakeProbe(), _ => 50L << 30, () => null)
            .RunAsync(null, null, TestContext.Current.CancellationToken);
        var home = Assert.Single(bad.Items, i => i.Level == DoctorLevel.Error && i.Name == "ARROW_HOME");
        Assert.Contains("my home", home.Detail, StringComparison.Ordinal);
        Assert.Contains("ARROW_HOME", home.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokenConfigurationIsAnErrorButTheRestIsStillChecked()
    {
        using var machine = Complete();
        File.WriteAllText(machine.Home.ConfigFile, "{ not json");
        var report = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);

        Assert.False(report.Ready);
        Assert.Equal("Cấu hình", Assert.Single(report.Items, i => i.Level == DoctorLevel.Error).Name);
        Assert.Contains(report.Items, i => i.Name == "git" && i.Level == DoctorLevel.Ok);
        Assert.Contains(report.Items, i => i.Name == "java-helper");

        File.WriteAllText(machine.Home.ConfigFile, "{ \"schema_version\": 1, \"verification\": { \"baseline_runs\": 0 } }");
        var invalid = await Doctor(machine).RunAsync(null, null, TestContext.Current.CancellationToken);
        var issue = Assert.Single(invalid.Items, i => i.Level == DoctorLevel.Error);
        Assert.Contains("verification.baseline_runs", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("arrow config set verification.baseline_runs", issue.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JavaHelperFailureAndWindowsLongPaths()
    {
        using var machine = Complete();
        var probe = new FakeProbe(failWith: ErrorCodes.JhStartFailed);
        var report = await Doctor(machine, probe, longPaths: () => false).RunAsync(null, null, TestContext.Current.CancellationToken);

        var helper = Assert.Single(report.Items, i => i.Name == "java-helper");
        Assert.Equal(DoctorLevel.Error, helper.Level);
        Assert.Contains("java-helper", helper.Detail, StringComparison.Ordinal);
        Assert.Equal(DoctorLevel.Warning, Assert.Single(report.Items, i => i.Name == "Đường dẫn dài").Level);
        Assert.False(report.Ready);
        Assert.Equal(1, report.Errors);

        // java-helper runs on the smallest JDK >= 17 (P10 §3.1).
        Assert.Equal(machine.Environment["JAVA_HOME_17_X64"], Path.GetDirectoryName(Path.GetDirectoryName(probe.Java))!);
    }

    private sealed class FakeTools(params (string Id, string? Maven, string File, string? Problem)[] tools) : IToolStore
    {
        public IReadOnlyList<ToolState> List() =>
            [.. tools.Select(t => new ToolState(new ToolEntry(t.Id, t.File, t.Maven, null, false, "00"), "/tools/" + t.File, t.Problem is null, t.Problem))];

        public Task<string> EnsureAsync(string id, IProgress<string>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> EnsureMavenArtifactAsync(string group, string artifact, string version, string folder, IProgress<string>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static readonly (string, string?, string)[] Lock =
    [
        ("jacoco-agent", "org.jacoco:org.jacoco.agent:0.8.12:jar:runtime", "jacoco-0.8.12/agent.jar"),
        ("pitest", "org.pitest:pitest:1.17.4", "pitest-1.17.4/pitest.jar"),
        ("pitest-testng-plugin", "org.pitest:pitest-testng-plugin:1.0.0", "pitest-1.17.4/testng.jar"),
        ("tsdetect", null, "tsdetect-2.2/TestSmellDetector.jar"),
    ];

    private static async Task<DoctorItem> ToolsLine(params string?[] problems)
    {
        using var machine = Complete();
        var tools = new FakeTools([.. Lock.Select((t, i) => (t.Item1, t.Item2, t.Item3, problems[i]))]);
        var doctor = new DoctorUseCase(machine.Config, machine.Home, machine.Discovery, machine.Runner, new FakeProbe(), _ => 50L * 1024 * 1024 * 1024, () => null, measuringTools: tools);
        var report = await doctor.RunAsync(null, null, TestContext.Current.CancellationToken);
        return Assert.Single(report.Items, i => i.Name == "JaCoCo / PIT / tsDetect");
    }

    // P08 §2.6, added in GD7: the doctor had no line for the measuring tools.
    [Fact]
    public async Task MeasuringToolsLineShowsVersionsAndState()
    {
        var ok = await ToolsLine(null, null, null, null);
        Assert.Equal((DoctorLevel.Ok, "0.8.12 / 1.17.4 / 2.2 · sha256 khớp"), (ok.Level, ok.Detail));

        // A plugin only some projects need is not a warning.
        var partial = await ToolsLine(null, null, "chưa tải", null);
        Assert.Equal(DoctorLevel.Ok, partial.Level);
        Assert.Contains("3/4 file đã tải", partial.Detail, StringComparison.Ordinal);

        var none = await ToolsLine("chưa tải", "chưa tải", "chưa tải", "chưa tải");
        Assert.Equal(DoctorLevel.Warning, none.Level);
        Assert.Contains("arrow tools install", none.Hint, StringComparison.Ordinal);

        var wrong = await ToolsLine(null, "sai sha256", null, null);
        Assert.Equal(DoctorLevel.Warning, wrong.Level);
        Assert.Contains("sai sha256: pitest", wrong.Detail, StringComparison.Ordinal);
    }
}
