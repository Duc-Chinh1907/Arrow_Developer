using Arrow.Business.Repair;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Quality;
using Microsoft.Extensions.DependencyInjection;

namespace Arrow.Cli.Tests;

/// <summary><c>arrow history</c> and <c>arrow show</c> (P08 §2.4, §2.7) with a fake use case.</summary>
public class ShowCommandTests
{
    private static readonly RunInfo Info = new("20261114-101010-abcd", "1.0.0", DateTimeOffset.Parse("2026-11-14T03:10:10Z", System.Globalization.CultureInfo.InvariantCulture), null,
        RunStatus.Completed, ["gen", "Order"], new RunProject(ProjectSourceKind.LocalDirectory, "shop", "D:/src/shop", null, null, null, null, null),
        new RunConfig("gpt-x", "api.example.com", 5, 3, true, true), new Dictionary<string, string>(), new Dictionary<string, string>(),
        [new RunTarget("com.acme.Order", TargetState.Done), new RunTarget("com.acme.Cart", TargetState.Pending)], new RunTotals(4, 9000, 3000, 0.0061));

    private static readonly TargetResult Order = new("com.acme.Order", TargetOutcome.Valid, VerificationState.Compile, 2, 2, StopReason.Valid,
        "com.acme.Order_ArrowTest", "src/test/java/com/acme/Order_ArrowTest.java", "block3/final.java", new TargetMetrics(0.78, 0.64, 0.86, 0.61, 0.7, 0.5),
        null, null, new TokenUsage(9000, 3000), 0.0061, 4, new BlockSeconds(10, 5, 20, 30, 65), null, [],
        new ApplySummary(ApplyDecision.Written, "src/test/java/com/acme/Order_ArrowTest.java"));

    private sealed class FakeShow(HistoryView? history = null) : IShowRunUseCase
    {
        public HistoryView History(int limit) => history ?? new HistoryView([new HistoryEntry(Info.RunId, Info.StartedAt, "shop", ["com.acme.Order"], 1, 1, RunStatus.Completed)], 1);

        public RunView Show(string run) => run == "nope"
            ? throw new ArrowException(ErrorCodes.GenResumeNotFound, args: new Dictionary<string, string> { ["run_id"] = run })
            : new RunView(Info, "/runs/" + Info.RunId, [(Info.Targets[0], Order), (Info.Targets[1], null)]);

        public TargetView ShowTarget(string run, string cut) => new(
            "com.acme.Order", Order, null, null,
            [
                new RoundView(0, null, "COMPILE · cannot find symbol | method", null, "checkpoint = c0"),
                new RoundView(1, 1, "mã trùng vòng 0", null, "lên mức 2"),
                new RoundView(2, 2, "VALID", Comparison.Better, "thành công"),
            ],
            new CoverageResult(0.78, 0.64, 0.86, new Dictionary<string, int>()),
            new MutationResult(54, 33, 11, 10, 0, 0, 0, 0.61, 0.75, [new SurvivorSample("totalPrice", 31, "ConditionalsBoundaryMutator")]),
            new SmellResult(6, new Dictionary<string, int> { ["Magic Number Test"] = 3 }, 3, 0.5),
            new Dictionary<string, string> { ["final"] = "/runs/x/final.java" });
    }

    private static CliRunner Cli(FakeShow show)
    {
        var cli = new CliRunner();
        cli.Services = services => services.AddSingleton<IShowRunUseCase>(show);
        return cli;
    }

    [Fact]
    public async Task HistoryListsRunsAndWarnsAboutDamagedLines()
    {
        using var cli = Cli(new FakeShow());
        var run = await cli.RunAsync("history", "--plain");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("20261114-101010-abcd", run.Out, StringComparison.Ordinal);
        Assert.Contains("COMPLETED", run.Out, StringComparison.Ordinal);
        Assert.Contains("Bỏ qua 1 dòng hỏng", run.Out + run.Error, StringComparison.Ordinal);

        using var empty = Cli(new FakeShow(new HistoryView([], 0)));
        Assert.Contains("Chưa có run nào", (await empty.RunAsync("history")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowPrintsTheTableOfTheRun()
    {
        using var cli = Cli(new FakeShow());
        var run = await cli.RunAsync("show", "20261114", "--plain");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Run 20261114-101010-abcd · COMPLETED", run.Out, StringComparison.Ordinal);
        Assert.Contains("Model gpt-x (api.example.com) · N=5 · có sửa · có đo", run.Out, StringComparison.Ordinal);
        Assert.Contains("78%", run.Out, StringComparison.Ordinal);
        Assert.Contains("PENDING", run.Out, StringComparison.Ordinal);
        Assert.Contains("0.0061 USD", run.Out, StringComparison.Ordinal);

        var missing = await cli.RunAsync("show", "nope");
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("GEN_RESUME_NOT_FOUND", missing.Out + missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowOfAClassPrintsRoundsMetricsAndSurvivors()
    {
        using var cli = Cli(new FakeShow());
        var run = await cli.RunAsync("show", "20261114", "Order", "--plain");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("checkpoint = c0", run.Out, StringComparison.Ordinal);
        Assert.Contains("tốt hơn", run.Out, StringComparison.Ordinal);
        Assert.Contains("mutation 61%", run.Out, StringComparison.Ordinal);
        Assert.Contains("totalPrice dòng 31 · ConditionalsBoundaryMutator", run.Out, StringComparison.Ordinal);
        Assert.Contains("Magic Number Test ×3", run.Out, StringComparison.Ordinal);

        var json = await cli.RunAsync("show", "20261114", "Order", "--json");
        Assert.Contains("\"survivors_sample\"", json.Out, StringComparison.Ordinal);
    }
}
