using System.Text.Json.Nodes;
using Arrow.Business.UseCases;
using Arrow.Common.Logging;
using Arrow.Common.Secrets;
using Arrow.DataAccess.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli.Tests;

/// <summary>The CLI skeleton of GĐ0 (P08 §2.1, §8, §10; P09 §3.4, §4.4).</summary>
public class CliAppTests
{
    private const string FakeKey = "sk-test-SECRET-123";

    [Fact]
    public async Task HelpListsTheCommandsAndGlobalOptions()
    {
        using var cli = new CliRunner();
        var run = await cli.RunAsync("--help");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("arrow [command] [options]", run.Out, StringComparison.Ordinal);
        foreach (var word in new[] { "config", "secrets", "--plain", "--json", "--verbose", "--yes", "--home", "--version" })
        {
            Assert.Contains(word, run.Out, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Parity: <c>ARROW_Framework/tests/Arrow.IntegrationTests/EndToEndC1Tests.UnknownCommandFailsWithCode2</c>, in-process here;
    /// the real executable is checked in Arrow.IntegrationTests. P08 §2.2: the error ends with how to get help.
    /// </summary>
    [Fact]
    public async Task UnknownCommandFailsWithCode2()
    {
        using var cli = new CliRunner();
        var run = await cli.RunAsync("nope");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("[X] CLI_ARGS", run.Out, StringComparison.Ordinal);
        Assert.Contains("\"arrow --help\"", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingArgumentPointsToTheHelpOfThatCommand()
    {
        using var cli = new CliRunner();
        var run = await cli.RunAsync("config", "set", "repair.rounds");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("\"arrow config set --help\"", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigSetThenListShowsTheValueAndItsOrigin()
    {
        using var cli = new CliRunner();
        Assert.Equal(0, (await cli.RunAsync("config", "set", "repair.rounds", "3")).ExitCode);
        Assert.Equal(0, (await cli.RunAsync("config", "set", "run.jobs", "2", "--project")).ExitCode);

        var list = await cli.RunAsync("config", "list", "--json");
        Assert.Equal(0, list.ExitCode);
        var entries = JsonNode.Parse(list.Out)!["entries"]!.AsArray();
        JsonNode Entry(string key) => entries.Single(e => e!["key"]!.GetValue<string>() == key)!;
        Assert.Equal((3, "user"), (Entry("repair.rounds")["value"]!.GetValue<int>(), Entry("repair.rounds")["origin"]!.GetValue<string>()));
        Assert.Equal((2, "project"), (Entry("run.jobs")["value"]!.GetValue<int>(), Entry("run.jobs")["origin"]!.GetValue<string>()));
        Assert.True(File.Exists(Path.Combine(cli.Project, ".arrow.json")));

        var plain = await cli.RunAsync("config", "list", "--origin");
        Assert.Matches(@"repair\.rounds\s+3\s+user", plain.Out);
    }

    [Fact]
    public async Task ConfigGetPrintsOneValueOrAGroup()
    {
        using var cli = new CliRunner();
        Assert.Equal("5", (await cli.RunAsync("config", "get", "repair.rounds")).Out.Trim());
        Assert.Contains("repair.early_stop_p", (await cli.RunAsync("config", "get", "repair")).Out, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, (await cli.RunAsync("config", "get", "repair.nope")).ExitCode);
    }

    [Fact]
    public async Task OutOfRangeValueIsRefusedAndTheFileStaysUntouched()
    {
        using var cli = new CliRunner();
        await cli.RunAsync("config", "set", "repair.rounds", "3");
        var before = File.ReadAllText(Path.Combine(cli.Home, "config.json"));

        var run = await cli.RunAsync("config", "set", "repair.early_stop_p", "2");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("CFG_INVALID", run.Out, StringComparison.Ordinal);
        Assert.Contains("repair.early_stop_p", run.Out, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(Path.Combine(cli.Home, "config.json")));
    }

    [Fact]
    public async Task ConfigUnsetGoesBackToTheDefault()
    {
        using var cli = new CliRunner();
        await cli.RunAsync("config", "set", "repair.rounds", "3");
        Assert.Contains("[OK]", (await cli.RunAsync("config", "unset", "repair.rounds")).Out, StringComparison.Ordinal);
        Assert.Contains("[!]", (await cli.RunAsync("config", "unset", "repair.rounds")).Out, StringComparison.Ordinal);
        Assert.Equal("5", (await cli.RunAsync("config", "get", "repair.rounds")).Out.Trim());
    }

    [Fact]
    public async Task BrokenConfigFileIsCfgParseWithExitCode2()
    {
        using var cli = new CliRunner();
        File.WriteAllText(Path.Combine(cli.Home, "config.json"), "{ \"repair\": ");
        var run = await cli.RunAsync("config", "list");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("[X] CFG_PARSE", run.Out, StringComparison.Ordinal);
        Assert.Contains("config.json", (await cli.RunAsync("config", "path")).Out, StringComparison.Ordinal);
    }

    /// <summary>PLAN GĐ0 "xong khi": the fake key appears in no output and in no file except the key store itself.</summary>
    [Fact]
    public async Task SecretFromStdinNeverLeaks()
    {
        using var cli = new CliRunner();
        var outputs = new List<CliRun>
        {
            await cli.RunWithInputAsync(FakeKey + "\n", "secrets", "set", "teamo", "--stdin"),
            await cli.RunAsync("secrets", "list"),
            await cli.RunAsync("secrets", "list", "--json"),
            await cli.RunAsync("config", "list", "--verbose"),
        };

        Assert.All(outputs, run => Assert.Equal(0, run.ExitCode));
        Assert.Contains("[OK] Đã lưu khoá teamo (...-123)", outputs[0].Out, StringComparison.Ordinal);
        Assert.Contains("teamo", outputs[1].Out, StringComparison.Ordinal);
        Assert.All(outputs, run => Assert.DoesNotContain(FakeKey, run.All, StringComparison.Ordinal));

        var leaking = cli.Files()
            .Where(f => f.Text.Contains(FakeKey, StringComparison.Ordinal))
            // On Linux and macOS the key store is a plain file with mode 600 by design (P09 §4.1).
            .Where(f => OperatingSystem.IsWindows() || !f.Path.Contains(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();
        Assert.Empty(leaking);
    }

    [Fact]
    public async Task SecretWithoutKeyboardOrStdinIsCliNoTty()
    {
        using var cli = new CliRunner();
        var run = await cli.RunAsync("secrets", "set", "teamo");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("CLI_NO_TTY", run.Out, StringComparison.Ordinal);
        Assert.Contains("--stdin", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Teamo")]
    [InlineData("../evil")]
    public async Task InvalidSecretNameIsAnArgumentError(string reference)
    {
        using var cli = new CliRunner();
        var run = await cli.RunWithInputAsync(FakeKey + "\n", "secrets", "set", reference, "--stdin");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("CLI_ARGS", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretsRemove()
    {
        using var cli = new CliRunner();
        await cli.RunWithInputAsync(FakeKey + "\n", "secrets", "set", "teamo", "--stdin");
        Assert.Contains("[OK]", (await cli.RunAsync("secrets", "remove", "teamo")).Out, StringComparison.Ordinal);
        Assert.Contains("[!]", (await cli.RunAsync("secrets", "remove", "teamo")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonModeWritesOnlyOneObjectToStdout()
    {
        using var cli = new CliRunner();
        var run = await cli.RunAsync("config", "set", "repair.rounds", "4", "--json");
        Assert.Equal("repair.rounds", JsonNode.Parse(run.Out)!["key"]!.GetValue<string>());
        Assert.Contains("[OK]", run.Error, StringComparison.Ordinal);

        var failed = await cli.RunAsync("config", "set", "repair.rounds", "abc", "--json");
        Assert.Equal("CFG_INVALID", JsonNode.Parse(failed.Out)!["error"]!["code"]!.GetValue<string>());
    }

    /// <summary>
    /// Parity: <c>ARROW_Framework/tests/Arrow.IntegrationTests/EndToEndC1Tests.ServicesWriteToolLog</c>.
    /// The log lives in <c>ARROW_HOME/logs</c> (P09 §7), not in the framework folder.
    /// </summary>
    [Fact]
    public async Task CommandsWriteTheToolLog()
    {
        using var cli = new CliRunner();
        await cli.RunAsync("config", "path");
        var log = Assert.Single(Directory.GetFiles(Path.Combine(cli.Home, "logs"), "arrow-*.log"));
        Assert.Contains("[INF] arrow config path", File.ReadAllText(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExceptionIsExitCode4WithTheLogPath()
    {
        using var cli = new CliRunner();
        using var output = new StringWriter();
        var console = new CliConsole(new StringReader(""), output, new StringWriter(), true, true, cli.Environment, cli.Project);
        var app = new CliApp(console, (c, globals) =>
        {
            var real = CompositionRoot.Build(c, globals);
            return new ServiceCollection()
                .AddSingleton(real.GetRequiredService<IAppLog>())
                .AddSingleton(real.GetRequiredService<ConfigUseCase>())
                .AddSingleton(new SecretsUseCase(new BrokenSecretStore()))
                .BuildServiceProvider();
        });

        var exit = await app.RunAsync(["secrets", "list", "--home", cli.Home], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Internal, exit);
        Assert.Contains("[X] CLI_INTERNAL", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(cli.Home, "logs"), output.ToString(), StringComparison.Ordinal);
        var log = Directory.GetFiles(Path.Combine(cli.Home, "logs")).Single();
        Assert.Contains("InvalidOperationException: disk on fire", File.ReadAllText(log), StringComparison.Ordinal);
    }

    private sealed class BrokenSecretStore : ISecretStore
    {
        public SecretValue? Get(string reference) => throw new InvalidOperationException("disk on fire");

        public void Set(string reference, SecretValue value) => throw new InvalidOperationException("disk on fire");

        public bool Remove(string reference) => throw new InvalidOperationException("disk on fire");

        public IReadOnlyList<SecretInfo> List() => throw new InvalidOperationException("disk on fire");
    }
}
