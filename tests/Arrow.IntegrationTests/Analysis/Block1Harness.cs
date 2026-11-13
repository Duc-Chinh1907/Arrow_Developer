using Arrow.Business.Analysis;
using Arrow.Common;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;
using Arrow.IntegrationTests.Fixtures;
using Arrow.IntegrationTests.JavaHelper;

namespace Arrow.IntegrationTests.Analysis;

/// <summary>
/// Block 1 wired with the real implementations, on a temporary <c>ARROW_HOME</c>: JDKs found on the machine (the
/// <c>JAVA_HOME_&lt;n&gt;_X64</c> variables on CI), Maven and Gradle from <c>PATH</c>, the embedded java-helper.
/// Anything missing skips the test (CLAUDE.md: tests never pass silently).
/// </summary>
public sealed class Block1Harness : IAsyncDisposable
{
    private static readonly Lazy<Task<IReadOnlyDictionary<int, string>>> MachineJdks = new(FindJdksAsync);

    private readonly JavaHelperPool _pool;

    private Block1Harness(IReadOnlyDictionary<int, string> jdks, string? maven, string? gradle)
    {
        Jdks = jdks;
        Home = new ArrowHome(Path.Combine(Path.GetTempPath(), "arrow-b1", Guid.NewGuid().ToString("N")[..8]));
        Home.EnsureCreated();
        Runs = new RunStore(Home);
        var runner = new ProcessRunner();
        var options = new AnalysisOptions(jdks, maven, gradle, BaselineRuns: 2, StepTimeout: TimeSpan.FromMinutes(10), InfraAttempts: 2, InfraWait: TimeSpan.Zero);
        _pool = new JavaHelperPool(JavaHelperEnvironment.Options(), 1);
        var git = new ToolLocator(Environment.GetEnvironmentVariable).FindOnPath("git") is { } gitPath ? new GitClient(runner, gitPath) : null;
        var workspaces = new WorkspaceManager();
        var catalog = ErrorCatalog.For("vi");
        var builds = new BuildExecutor(runner, Environment.GetEnvironmentVariable, BuildFailureClassifier.UserCaches(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Analyzer = new ProjectAnalyzer(
            new ProjectSource(Home.MirrorsDir, git, workspaces, []),
            ct => _pool.RentAsync(ct),
            new ModuleAnalyzer(builds, workspaces, options, catalog),
            Runs,
            catalog);
    }

    public ArrowHome Home { get; }

    /// <summary>JDK homes by major found on this machine.</summary>
    public IReadOnlyDictionary<int, string> Jdks { get; }

    public RunStore Runs { get; }

    public ProjectAnalyzer Analyzer { get; }

    public List<ArrowEvent> Events { get; } = [];

    /// <summary>A harness; skips the test when a JDK, java-helper, or the build tool the fixture needs is missing.</summary>
    public static async Task<Block1Harness> CreateAsync(bool needsGradle = false)
    {
        var jdks = await MachineJdks.Value;
        Assert.SkipWhen(jdks.Count == 0, "no JDK found (JAVA_HOME_<n>_X64, JAVA_HOME, PATH)");
        var tools = new ToolLocator(Environment.GetEnvironmentVariable);
        var maven = tools.FindOnPath("mvn");
        var gradle = tools.FindOnPath("gradle");
        Assert.SkipWhen(!needsGradle && maven is null, "Maven is not on PATH");
        Assert.SkipWhen(needsGradle && gradle is null, "Gradle is not on PATH");
        return new Block1Harness(jdks, maven, gradle);
    }

    /// <summary>A new run with <c>command = inspect</c>.</summary>
    public RunContext NewRun()
    {
        var id = Runs.NewUniqueRunId(DateTime.Now);
        var info = new RunInfo(id, "test", DateTimeOffset.UtcNow, null, RunStatus.Running, ["inspect"],
            new RunProject(ProjectSourceKind.LocalDirectory, "fixture", null, null, null, null, null, null), null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), [], RunTotals.Zero);
        var handle = Runs.Create(info);
        var work = Path.Combine(Home.WorkDir, id);
        Directory.CreateDirectory(work);
        return new RunContext(id, Home.Root, work, handle, new SyncProgress(Events));
    }

    /// <summary>Prepare, locate (must find exactly one class) and analyse, as <c>arrow inspect</c> does.</summary>
    public async Task<(ProjectAnalysis Analysis, RunContext Run)> InspectAsync(string projectDir, string query)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = NewRun();
        var workspace = await Analyzer.PrepareAsync(new ProjectTarget(ProjectSourceKind.LocalDirectory, projectDir, null, query), run, null, ct);
        var located = Assert.IsType<LocateResult.Found>(await Analyzer.LocateAsync(workspace, query, ct));
        return (await Analyzer.AnalyzeAsync(workspace, located.Class, run, ct), run);
    }

    /// <summary>A file of <c>targets/&lt;cut&gt;/block1/</c>, or null.</summary>
    public string? Block1File(RunContext run, string cut, string name) =>
        Runs.ReadText(run.Run, $"targets/{cut}/block1/{name}");

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
        WorkspaceManager.DeleteTree(Home.Root);
    }

    private static async Task<IReadOnlyDictionary<int, string>> FindJdksAsync()
    {
        var locator = new JdkLocator(new ProcessRunner(), Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var found = await locator.FindAsync(new Dictionary<string, string>(), CancellationToken.None);
        return JdkLocator.ByMajor(found).Where(j => JavaVersionsSupported.Contains(j.Key)).ToDictionary(j => j.Key, j => j.Value.Home);
    }

    private static readonly HashSet<int> JavaVersionsSupported = [8, 11, 17, 21];

    /// <summary>Records events on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SyncProgress(List<ArrowEvent> events) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value)
        {
            lock (events)
            {
                events.Add(value);
            }
        }
    }
}

/// <summary>A fixture copy that stands for the user's project; its fingerprint must not change (P11 §2).</summary>
public sealed class UserProject : IDisposable
{
    private readonly FixtureRepo _repo;

    public UserProject(string fixture)
    {
        _repo = new FixtureRepo(fixture);
        Fingerprint = FixtureRepo.FingerprintOf(_repo.Root);
    }

    public string Root => _repo.Root;

    public string Fingerprint { get; }

    public void AssertUnchanged() => Assert.Equal(Fingerprint, FixtureRepo.FingerprintOf(Root));

    public void Dispose() => _repo.Dispose();
}
