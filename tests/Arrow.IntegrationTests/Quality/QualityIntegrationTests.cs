using Arrow.Business.Quality;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Quality;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;
using Arrow.IntegrationTests.Analysis;
using Arrow.IntegrationTests.Fixtures;
using Arrow.IntegrationTests.JavaHelper;

namespace Arrow.IntegrationTests.Quality;

/// <summary>
/// Block 4 on the 4 fixtures of P05 §10 with the hand-written VALID suites of <c>.arrow-sample/</c>, the real build tools, the
/// tools of <c>tools.lock.json</c> and java-helper. PLAN GĐ5 "xong khi": the 3 metrics on every fixture.
/// </summary>
[Trait("Category", "Integration")]
public class QualityIntegrationTests
{
    /// <summary>Tools are downloaded once for every test of the class run.</summary>
    private static readonly ArrowHome ToolsHome = new(Path.Combine(Path.GetTempPath(), "arrow-quality-tools"));

    private static async Task<(QualityResult Result, Block1Harness Harness, UserProject Project, TaskContext Task)> MeasureAsync(string fixture, string cut, string sample, bool gradle = false)
    {
        var harness = await Block1Harness.CreateAsync(gradle);
        var project = new UserProject(fixture);
        var (analysis, run) = await harness.InspectAsync(project.Root, cut);
        Assert.True(analysis.Outcome.Outcome == BlockOutcomeKind.Ok, analysis.Outcome.Message + " " + analysis.Outcome.FirstError);
        var m = analysis.Metadata!;
        var cutSource = File.ReadAllText(Path.Combine(harness.Home.Root, analysis.Workspace!.Base, m.Paths.CutPath));
        var task = new TaskContext(run, m.Cut.Fqn, m, analysis.Baseline!, analysis.Workspace, cutSource, $"targets/{m.Cut.Fqn}", []);

        ToolsHome.EnsureCreated();
        var helper = JavaHelperEnvironment.Options();
        await using var pool = new JavaHelperPool(helper, 1);
        var store = new ToolStore(ToolsHome, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, Environment.GetEnvironmentVariable);
        var builds = new BuildExecutor(new ProcessRunner(), Environment.GetEnvironmentVariable,
            BuildFailureClassifier.UserCaches(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var workspaces = new WorkspaceManager();
        var java = new JavaRunner(new ProcessRunner());
        var preparer = new SuitePreparer(builds, workspaces);
        var service = new QualityService(
        [
            new JacocoTool(preparer, builds, store, java, workspaces, harness.Runs),
            new PitTool(preparer, store, java, workspaces, harness.Runs),
            new TsDetectTool(store, pool, java, workspaces, harness.Runs),
        ], harness.Runs, ErrorCatalog.For("vi"));

        var code = File.ReadAllText(Path.Combine(FixtureRepo.FixturesDir, fixture, ".arrow-sample", sample));
        var options = new QualityOptions(QualityService.Order, harness.Jdks, helper.JavaExecutable,
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(5), 2, TimeSpan.Zero, false);
        var result = await service.MeasureAsync(new SuiteToMeasure(m.Paths.GeneratedTestFqn, m.Paths.GeneratedTestPath, code), task, options, TestContext.Current.CancellationToken);
        Assert.SkipWhen(result.Tools.Values.Any(t => t.Code == ErrorCodes.CfgToolMissing), "the tools of block 4 cannot be downloaded here (no network)");
        return (result, harness, project, task);
    }

    private static void AssertAllOk(QualityResult result)
    {
        foreach (var (tool, outcome) in result.Tools)
        {
            Assert.True(outcome.Status == ToolStatus.Ok, $"{tool}: {outcome.Code} {outcome.Message}");
        }

        Assert.Equal(["jacoco", "pit", "tsdetect"], result.Tools.Keys.Order(StringComparer.Ordinal));
    }

    private static async Task FinishAsync(Block1Harness harness, UserProject project, TaskContext task)
    {
        // Every copy of block 4 was deleted, and the user's project did not change.
        Assert.Empty(Directory.EnumerateDirectories(task.Run.WorkDir, "q-*"));
        Assert.Empty(Directory.EnumerateDirectories(task.Run.WorkDir, "ts-*"));
        project.AssertUnchanged();
        project.Dispose();
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task MavenJUnit4()
    {
        var (result, harness, project, task) = await MeasureAsync("maven-verify-junit4", "PriceCalculator", "PriceCalculator_ArrowTest.java");
        AssertAllOk(result);
        Assert.Equal((1.0, 1.0, 1.0), (result.Coverage!.Line!.Value, result.Coverage.Branch!.Value, result.Coverage.Method!.Value));
        Assert.InRange(result.Mutation!.Score!.Value, 0.5, 1.0);
        Assert.Equal(0, result.Mutation.NoCoverage);
        Assert.Equal(4, result.Smells!.TestMethods);
        Assert.True(harness.Runs.Exists(task.Run.Run, $"{task.TargetDir}/block4/outcome.json"));
        Assert.True(harness.Runs.Exists(task.Run.Run, $"{task.TargetDir}/block4/pit.json"));
        await FinishAsync(harness, project, task);
    }

    // P05 §10: PIT gets junit-platform-launcher of the module's version; JaCoCo measures shop-core only.
    [Fact]
    public async Task MavenMultiModuleJUnit5()
    {
        var (result, harness, project, task) = await MeasureAsync("maven-multi-junit5", "OrderService", "OrderService_ArrowTest.java");
        AssertAllOk(result);
        Assert.Equal((1.0, 1.0), (result.Coverage!.Line!.Value, result.Coverage.Branch!.Value));
        Assert.InRange(result.Mutation!.Score!.Value, 0.5, 1.0);
        var pitLog = File.ReadAllText(Path.Combine(task.Run.Run.Root, "targets", "com.acme.shop.OrderService", "block4", "logs", "pit.log"));
        Assert.Contains("junit-platform-launcher-1.10.2.jar", pitLog, StringComparison.Ordinal);
        Assert.Contains("--targetClasses com.acme.shop.OrderService,com.acme.shop.OrderService$*", pitLog, StringComparison.Ordinal);
        await FinishAsync(harness, project, task);
    }

    [Fact]
    public async Task GradleJUnit5()
    {
        var (result, harness, project, task) = await MeasureAsync("gradle-junit5", "Calculator", "Calculator_ArrowTest.java", gradle: true);
        AssertAllOk(result);
        Assert.Equal(1.0, result.Coverage!.Line);
        Assert.Null(result.Coverage.Branch); // Calculator has no branch
        Assert.NotNull(result.Mutation!.Score);
        Assert.Equal(2, result.Smells!.TestMethods);
        await FinishAsync(harness, project, task);
    }

    [Fact]
    public async Task MavenTestNG()
    {
        var (result, harness, project, task) = await MeasureAsync("maven-testng", "Stock", "Stock_ArrowTest.java");
        AssertAllOk(result);

        // The sample never adds a positive amount and never sees a non-empty isEmpty() (README of the fixture).
        Assert.Equal((0.9048, 0.8571, 1.0), (result.Coverage!.Line!.Value, result.Coverage.Branch!.Value, result.Coverage.Method!.Value));
        Assert.InRange(result.Mutation!.Score!.Value, 0.5, 1.0);
        Assert.Contains("pitest-testng-plugin", File.ReadAllText(Path.Combine(task.Run.Run.Root, "targets", "com.acme.inventory.Stock", "block4", "logs", "pit.log")), StringComparison.Ordinal);
        Assert.Equal(5, result.Smells!.TestMethods);
        await FinishAsync(harness, project, task);
    }
}
