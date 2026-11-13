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

    private async Task<ProcessResult> Arrow(string stdin, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Arrow.Cli.dll");
        var spec = new ProcessSpec("dotnet", [dll, .. args, "--home", _home], _home,
            Environment: new Dictionary<string, string?> { ["NO_COLOR"] = "1" },
            Timeout: TimeSpan.FromMinutes(1),
            StandardInput: stdin);
        return await new ProcessRunner().RunAsync(spec, TestContext.Current.CancellationToken);
    }
}
