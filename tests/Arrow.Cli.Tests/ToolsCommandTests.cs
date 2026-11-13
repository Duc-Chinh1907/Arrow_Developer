using Arrow.Business.UseCases;
using Arrow.Common.Errors;
using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli.Tests;

/// <summary><c>arrow tools list|install</c> (P09 §6) with a fake use case.</summary>
public class ToolsCommandTests
{
    private sealed class FakeTools : IToolsUseCase
    {
        public IReadOnlyList<string>? Installed { get; private set; }

        public IReadOnlyList<ToolRow> List() =>
        [
            new("jacoco-agent", "org.jacoco:org.jacoco.agent:0.8.12:jar:runtime", "/h/tools/jacocoagent.jar", true, null),
            new("tsdetect", "https://github.com/x.jar", "/h/tools/TestSmellDetector.jar", false, "chưa tải"),
        ];

        public Task<IReadOnlyList<ToolInstallResult>> InstallAsync(IReadOnlyList<string> ids, IProgress<string>? progress, CancellationToken ct)
        {
            Installed = ids;
            progress?.Report("Tải tsdetect từ github.com");
            return Task.FromResult<IReadOnlyList<ToolInstallResult>>(
            [
                new("jacoco-agent", "/h/tools/jacocoagent.jar", null),
                new("tsdetect", null, new ErrorInfo(ErrorCodes.CfgToolMissing, "tsdetect: không tải được", "Chạy arrow tools install khi có mạng", null)),
            ]);
        }
    }

    private static CliRunner Cli(FakeTools tools)
    {
        var cli = new CliRunner();
        cli.Services = services => services.AddSingleton<IToolsUseCase>(tools);
        return cli;
    }

    [Fact]
    public async Task ListShowsEachToolAndHowToInstallTheMissingOnes()
    {
        using var cli = Cli(new FakeTools());
        var run = await cli.RunAsync("tools", "list");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("jacoco-agent", run.Out, StringComparison.Ordinal);
        Assert.Contains("chưa tải", run.Out, StringComparison.Ordinal);
        Assert.Contains("arrow tools install", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallReportsEveryToolAndFailsWhenOneFails()
    {
        var tools = new FakeTools();
        using var cli = Cli(tools);
        var run = await cli.RunAsync("tools", "install", "jacoco-agent", "tsdetect");
        Assert.Equal(2, run.ExitCode);
        Assert.Equal(["jacoco-agent", "tsdetect"], tools.Installed);
        Assert.Contains("Tải tsdetect từ github.com", run.Out, StringComparison.Ordinal);
        Assert.Contains("jacoco-agent: /h/tools/jacocoagent.jar", run.Out, StringComparison.Ordinal);
        Assert.Contains("CFG_TOOL_MISSING", run.Out + run.Error, StringComparison.Ordinal);

        var json = await cli.RunAsync("tools", "install", "--json");
        Assert.Empty(tools.Installed!);
        Assert.Contains("\"tools\"", json.Out, StringComparison.Ordinal);
    }
}
