using Arrow.DataAccess.Processes;

namespace Arrow.IntegrationTests;

/// <summary>
/// The real <c>Arrow.Cli</c> executable, started as a separate process (P11 §1, E2E level).
/// Parity: <c>ARROW_Framework/tests/Arrow.IntegrationTests/EndToEndC1Tests</c> (the call goes through all layers).
/// </summary>
[Trait("Category", "E2E")]
public sealed class CliExecutableTests : IDisposable
{
    private const string FakeKey = "sk-test-SECRET-123";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "arrow-e2e", Guid.NewGuid().ToString("N")[..12]);

    public CliExecutableTests()
    {
        Directory.CreateDirectory(_home);
    }

    [Fact]
    public async Task HelpExitsZero()
    {
        var run = await Arrow("", "--help");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("arrow [command] [options]", run.Output, StringComparison.Ordinal);
    }

    /// <summary>Parity: <c>EndToEndC1Tests.UnknownCommandFailsWithCode2</c>.</summary>
    [Fact]
    public async Task UnknownCommandFailsWithCode2()
    {
        var run = await Arrow("", "nope");
        Assert.Equal(2, run.ExitCode);
        Assert.Contains("[X] CLI_ARGS", run.Output, StringComparison.Ordinal);
    }

    /// <summary>PLAN GĐ0 "xong khi": config set/list and secrets set --stdin work, and the fake key leaks nowhere.</summary>
    [Fact]
    public async Task ConfigAndSecretsWorkEndToEnd()
    {
        Assert.Equal(0, (await Arrow("", "config", "set", "repair.rounds", "3")).ExitCode);
        var list = await Arrow("", "config", "list", "--origin");
        Assert.Matches(@"repair\.rounds\s+3\s+user", list.Output);

        var set = await Arrow(FakeKey + "\n", "secrets", "set", "e2e-test", "--stdin");
        Assert.Equal(0, set.ExitCode);
        Assert.DoesNotContain(FakeKey, set.Output, StringComparison.Ordinal);

        var leaking = Directory.GetFiles(_home, "*", SearchOption.AllDirectories)
            .Where(f => OperatingSystem.IsWindows() || !f.Contains(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(FakeKey, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(leaking);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_home, "logs"), "arrow-*.log"));
    }

    /// <summary>P08 §2.6: the machine of the tests (CI runners, a developer machine) is ready.</summary>
    [Fact]
    public async Task DoctorReportsAReadyMachine()
    {
        var run = await Arrow("", TimeSpan.FromMinutes(3), "doctor");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("java-helper", run.Output, StringComparison.Ordinal);
        Assert.Contains("Kết quả: SẴN SÀNG", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// P01 §11, P11 §2: <c>arrow inspect</c> through the real executable on a fixture, with the JSON result; the project is not changed
    /// and the work folder is deleted afterwards.
    /// </summary>
    [Fact]
    public async Task InspectFixtureWithJson()
    {
        Assert.SkipWhen(new Arrow.DataAccess.Tools.ToolLocator(Environment.GetEnvironmentVariable).FindOnPath("mvn") is null, "Maven is not on PATH");
        using var project = new Fixtures.FixtureRepo("maven-existing-arrowtest");
        var before = Fixtures.FixtureRepo.FingerprintOf(project.Root);

        var run = await Arrow("", TimeSpan.FromMinutes(8), "inspect", "WordCounter", "--project", project.Root, "--json");

        Assert.True(run.ExitCode == 0, run.Output);
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonObjectOf(run.Output))!;
        var analysis = json["analysis"]!;
        Assert.Equal("OK", (string?)json["outcome"]!["outcome"]);
        Assert.Equal("com.acme.text.WordCounter_Arrow2Test", (string?)analysis["cut"]!["generated_test_fqn"]);
        Assert.Equal(2, (int)analysis["cut"]!["name_index"]!);
        Assert.Single(analysis["baseline"]!["passing_in_all_runs"]!.AsArray());
        Assert.Equal(before, Fixtures.FixtureRepo.FingerprintOf(project.Root));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_home, "work")));
        Assert.True(File.Exists(Path.Combine((string)json["run_dir"]!, "targets", "com.acme.text.WordCounter", "block1", "outcome.json")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The JSON object in a mixed stdout/stderr text: the lines from the first line that is <c>{</c> to the last that is <c>}</c>.</summary>
    private static string JsonObjectOf(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = Array.IndexOf(lines, "{");
        var last = Array.LastIndexOf(lines, "}");
        return string.Join('\n', lines[first..(last + 1)]);
    }

    private Task<ProcessResult> Arrow(string stdin, params string[] args) => Arrow(stdin, TimeSpan.FromMinutes(1), args);

    private async Task<ProcessResult> Arrow(string stdin, TimeSpan timeout, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Arrow.Cli.dll");
        var spec = new ProcessSpec("dotnet", [dll, .. args, "--home", _home], _home,
            Environment: new Dictionary<string, string?> { ["NO_COLOR"] = "1" },
            Timeout: timeout,
            StandardInput: stdin);
        return await new ProcessRunner().RunAsync(spec, TestContext.Current.CancellationToken);
    }
}
