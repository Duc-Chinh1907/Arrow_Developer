using Arrow.Business.Analysis;
using Arrow.Business.Quality;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Quality;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.Quality;

/// <summary>Order, retries and isolation of the tools of block 4 (P05 §7, §10), with fake tools.</summary>
public sealed class QualityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-quality", Guid.NewGuid().ToString("N")[..8]);
    private readonly RunStore _runs;
    private readonly RunHandle _run;
    private readonly List<ArrowEvent> _events = [];

    public QualityServiceTests()
    {
        var home = new ArrowHome(_root);
        home.EnsureCreated();
        _runs = new RunStore(home);
        _run = _runs.Create(new RunInfo("r1", "test", DateTimeOffset.UtcNow, null, RunStatus.Running, ["gen"],
            new RunProject(ProjectSourceKind.LocalDirectory, "p", null, null, null, null, null, null), null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), [], RunTotals.Zero));
    }

    public void Dispose()
    {
        try
        {
            WorkspaceManager.DeleteTree(_root);
        }
        catch (IOException)
        {
        }
    }

    private TaskContext Task() => new(
        new RunContext("r1", _root, Path.Combine(_root, "work", "r1"), _run, new ListProgress(_events)),
        "a.Order", null!, null!, null!, "", "targets/a.Order", []);

    private static QualityOptions Options(params string[] tools) =>
        new(tools.Length == 0 ? QualityService.Order : tools, new Dictionary<int, string>(), "java", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 3, TimeSpan.FromSeconds(30), false);

    /// <summary>Answers from a queue: a result, or an exception.</summary>
    private sealed class FakeTool(string name, params object[] answers) : IQualityTool
    {
        public int Calls { get; private set; }

        public string Name => name;

        public Task<object> MeasureAsync(SuiteToMeasure suite, TaskContext task, QualityOptions options, CancellationToken ct)
        {
            var answer = answers[Math.Min(Calls++, answers.Length - 1)];
            return answer is Exception ex ? System.Threading.Tasks.Task.FromException<object>(ex) : System.Threading.Tasks.Task.FromResult(answer);
        }
    }

    private sealed class ImmediateTime : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Delays.Add(dueTime);
                callback(state);
            }

            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static readonly CoverageResult Coverage = new(0.78, 0.64, 0.86, new Dictionary<string, int> { ["LINE_COVERED"] = 39 });
    private static readonly MutationResult Mutation = new(54, 30, 12, 10, 2, 0, 0, 0.5926, 0.6818, []);
    private static readonly SmellResult Smells = new(6, new Dictionary<string, int> { ["Assertion Roulette"] = 3 }, 3, 0.5);
    private static readonly SuiteToMeasure Suite = new("a.Order_ArrowTest", "src/test/java/a/Order_ArrowTest.java", "class Order_ArrowTest {}");

    [Fact]
    public async Task AllToolsGiveTheMetricsAndTheFiles()
    {
        var service = new QualityService([new FakeTool("tsdetect", Smells), new FakeTool("pit", Mutation), new FakeTool("jacoco", Coverage)], _runs, ErrorCatalog.For("vi"));
        var result = await service.MeasureAsync(Suite, Task(), Options(), TestContext.Current.CancellationToken);

        Assert.Equal(new TargetMetrics(0.78, 0.64, 0.86, 0.5926, 0.6818, 0.5), result.Metrics);
        Assert.All(result.Tools.Values, t => Assert.Equal(ToolStatus.Ok, t.Status));
        Assert.Equal(["jacoco", "pit", "tsdetect"], _events.Where(e => e.Type == ArrowEventTypes.B4ToolStart).Select(e => (string)e.Data["tool"]!));
        Assert.Equal("pit 1.17.4", _runs.ReadJson<MutationResult>(_run, "targets/a.Order/block4/pit.json")!.Tool);
        Assert.Contains("\"status\": \"OK\"", _runs.ReadText(_run, "targets/a.Order/block4/outcome.json"), StringComparison.Ordinal);
        Assert.NotNull(_runs.ReadText(_run, "targets/a.Order/block4/jacoco.json"));
        Assert.NotNull(_runs.ReadText(_run, "targets/a.Order/block4/tsdetect.json"));
        Assert.Contains(_events, e => e.Type == ArrowEventTypes.B4Done);
    }

    // P05 §7: one tool failing does not stop the others.
    [Fact]
    public async Task OneFailingToolDoesNotStopTheOthers()
    {
        var pit = new FakeTool("pit", new ToolFailure(ErrorCodes.B4PitScope, "PIT tạo mutant cho class ngoài CUT: a.OrderService"));
        var service = new QualityService([new FakeTool("jacoco", Coverage), pit, new FakeTool("tsdetect", Smells)], _runs, ErrorCatalog.For("vi"));
        var result = await service.MeasureAsync(Suite, Task(), Options(), TestContext.Current.CancellationToken);

        Assert.Equal((ToolStatus.Failed, ErrorCodes.B4PitScope, 1), (result.Tools["pit"].Status, result.Tools["pit"].Code, result.Tools["pit"].Attempts));
        Assert.Equal(1, pit.Calls); // not an environment failure: no retry
        Assert.Null(result.Mutation);
        Assert.Equal((0.78, 0.5), (result.Metrics.Line!.Value, result.Metrics.SmellDensity!.Value));
        Assert.Null(result.Metrics.Mutation);
        Assert.Null(_runs.ReadText(_run, "targets/a.Order/block4/pit.json"));
        Assert.Contains(_events, e => e.Type == ArrowEventTypes.B4ToolFailed && e.Level == EventLevel.Warning);
    }

    // P05 §7: an environment failure is tried again, at most 3 times, 30 s apart.
    [Fact]
    public async Task EnvironmentFailuresAreTriedAgain()
    {
        var time = new ImmediateTime();
        var jacoco = new FakeTool("jacoco", new ToolFailure(ErrorCodes.B4JacocoFailed, "chạy test quá 900 s", retryable: true), Coverage);
        var pit = new FakeTool("pit", new ToolFailure(ErrorCodes.B4PitFailed, "PIT quá 30 phút", retryable: true));
        var service = new QualityService([jacoco, pit], _runs, ErrorCatalog.For("vi"), time);
        var result = await service.MeasureAsync(Suite, Task(), Options("jacoco", "pit"), TestContext.Current.CancellationToken);

        Assert.Equal((ToolStatus.Ok, 2), (result.Tools["jacoco"].Status, result.Tools["jacoco"].Attempts));
        Assert.Equal((ToolStatus.Failed, 3, 3), (result.Tools["pit"].Status, result.Tools["pit"].Attempts, pit.Calls));
        Assert.Equal(3, time.Delays.Count);
        Assert.All(time.Delays, d => Assert.Equal(TimeSpan.FromSeconds(30), d));
        Assert.False(result.Tools.ContainsKey("tsdetect"));
    }

    // P05 §7: a tool that cannot be downloaded is SKIPPED with CFG_TOOL_MISSING.
    [Fact]
    public async Task AMissingToolIsSkipped()
    {
        var service = new QualityService([new FakeTool("jacoco", new ArrowException(ErrorCodes.CfgToolMissing, "jacoco-agent: không tải được"))], _runs, ErrorCatalog.For("vi"));
        var result = await service.MeasureAsync(Suite, Task(), Options("jacoco"), TestContext.Current.CancellationToken);
        Assert.Equal((ToolStatus.Skipped, ErrorCodes.CfgToolMissing), (result.Tools["jacoco"].Status, result.Tools["jacoco"].Code));
        Assert.Contains("\"status\": \"SKIPPED\"", _runs.ReadText(_run, "targets/a.Order/block4/outcome.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void PercentsAreWholeNumbers()
    {
        Assert.Equal("78%", QualityService.Percent(0.7812));
        Assert.Equal("-", QualityService.Percent(null));
    }

    private sealed class ListProgress(List<ArrowEvent> events) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value) => events.Add(value);
    }
}
