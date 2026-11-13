using Arrow.Business.Analysis;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.UseCases;

/// <summary><c>arrow inspect</c> (P07 §11): prechecks, the run it creates, and how the answers of block 1 become a result.</summary>
public class InspectUseCaseTests
{
    private sealed class FakeAnalyzer : IProjectAnalyzer
    {
        public Func<ProjectWorkspace, string, LocateResult> Locate { get; set; } = (_, _) => throw new InvalidOperationException();

        public Exception? PrepareFails { get; set; }

        public Exception? AnalyzeFails { get; set; }

        public bool CleanedUp { get; private set; }

        public bool? CleanedUpKeep { get; private set; }

        public Task<ProjectWorkspace> PrepareAsync(ProjectTarget target, RunContext run, Func<long, Task<bool>>? confirmLargeCopy, CancellationToken ct)
        {
            if (PrepareFails is not null)
            {
                throw PrepareFails;
            }

            run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, ArrowEventTypes.B1Checkout, null, null, null, EventLevel.Info, "checkout", new Dictionary<string, object?>()));
            var checkout = new DataAccess.Source.SourceCheckout(Path.Combine(run.WorkDir, "base"), target.Kind, target.Location, "MyShop", null, null, "abc", false, "sha256:1", null, []);
            return Task.FromResult(new ProjectWorkspace(checkout, new DataAccess.Build.BuildLayout([]), [], [], ["from-checkout"], 0.1, 0.2));
        }

        public Task<LocateResult> LocateAsync(ProjectWorkspace workspace, string query, CancellationToken ct) => Task.FromResult(Locate(workspace, query));

        public Task<ProjectAnalysis> AnalyzeAsync(ProjectWorkspace workspace, ClassCandidate cut, RunContext run, CancellationToken ct)
        {
            if (AnalyzeFails is not null)
            {
                throw AnalyzeFails;
            }

            var cutInfo = new CutInfo(cut.Fqn, cut.SimpleName, cut.Package, cut.Kind, cut.Path, "sha", "t/X_ArrowTest.java", cut.Fqn + "_ArrowTest", 1);
            return Task.FromResult(new ProjectAnalysis(BlockOutcome.Ok(1.5), null, null, null, cutInfo, ["warn-from-analysis"], new StepTimings(0.1, 0.2, 0.3, 0.4, [1, 2], 1.5), null));
        }

        public Task CleanupAsync(ProjectWorkspace workspace, RunContext run, bool keep, CancellationToken ct)
        {
            (CleanedUp, CleanedUpKeep) = (true, keep);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFactory(FakeAnalyzer analyzer) : IAnalyzerHostFactory
    {
        public AnalyzerSetup? Setup { get; private set; }

        public bool Disposed { get; private set; }

        public Exception? StartFails { get; set; }

        public Task<IAnalyzerHost> StartAsync(AnalyzerSetup setup, CancellationToken ct)
        {
            Setup = setup;
            return StartFails is null ? Task.FromResult<IAnalyzerHost>(new Host(this, analyzer)) : throw StartFails;
        }

        private sealed class Host(FakeFactory owner, FakeAnalyzer analyzer) : IAnalyzerHost
        {
            public IProjectAnalyzer Analyzer { get; } = analyzer;

            public Arrow.DataAccess.JavaHelper.IJavaHelperProvider JavaHelpers => throw new NotSupportedException("inspect does not use java-helper directly");

            public Arrow.Business.Repair.IVerifier Verifier => throw new NotSupportedException("inspect does not verify");

            public Arrow.DataAccess.Build.IBuildExecutor Builds => throw new NotSupportedException("inspect does not measure");

            public Arrow.DataAccess.Source.IWorkspaceManager Workspaces => throw new NotSupportedException("inspect does not measure");

            public ValueTask DisposeAsync()
            {
                owner.Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeConfirmation(int? choice) : IConfirmation
    {
        public bool CanAsk => true;

        public List<IReadOnlyList<string>> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string question, bool defaultAnswer, CancellationToken ct) => Task.FromResult(defaultAnswer);

        public Task<IReadOnlyList<int>?> ChooseManyAsync(string question, IReadOnlyList<string> options, IReadOnlyList<bool> preselected, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>?>(null);

        public Task<int?> ChooseOneAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
        {
            Asked.Add(options);
            return Task.FromResult(choice);
        }
    }

    private sealed class SyncProgress(List<ArrowEvent> events) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value) => events.Add(value);
    }

    private static ClassCandidate Class(string fqn, CutKind kind = CutKind.Class) =>
        new(fqn, fqn[(fqn.LastIndexOf('.') + 1)..], kind, false, "src/main/java/" + fqn.Replace('.', '/') + ".java");

    private static ProjectTarget Local(FakeMachine machine, string query = "Order") => new(ProjectSourceKind.LocalDirectory, Path.Combine(machine.Root, "MyShop"), null, query);

    private static (InspectUseCase UseCase, FakeFactory Factory, FakeAnalyzer Analyzer) Create(FakeMachine machine, Func<string, long?>? free = null)
    {
        var analyzer = new FakeAnalyzer { Locate = (_, _) => new LocateResult.Found(Class("com.shop.Order"), null) };
        var factory = new FakeFactory(analyzer);
        var useCase = new InspectUseCase(machine.Config, machine.Home, machine.Discovery, new RunStore(machine.Home), factory, "1.2.3", free ?? (_ => 50L << 30));
        return (useCase, factory, analyzer);
    }

    private static FakeMachine WithJdk17()
    {
        var machine = new FakeMachine();
        machine.AddJdk(17, "17.0.12");
        return machine;
    }

    private static Task<InspectResult> Run(InspectUseCase useCase, ProjectTarget target, IConfirmation? confirm = null, IProgress<ArrowEvent>? progress = null) =>
        useCase.RunAsync(target, ["inspect", target.CutQuery], null, progress, confirm ?? new NoConfirmation(), TestContext.Current.CancellationToken);

    private static string[] Runs(FakeMachine machine) => Directory.Exists(machine.Home.RunsDir) ? Directory.GetDirectories(machine.Home.RunsDir) : [];

    [Fact]
    public async Task CreatesAnInspectRunWithEventsAndCleansUp()
    {
        using var machine = WithJdk17();
        var (useCase, factory, analyzer) = Create(machine);
        var shown = new List<ArrowEvent>();

        var result = await Run(useCase, Local(machine), progress: new SyncProgress(shown));
        Assert.Contains(shown, e => e.Type == ArrowEventTypes.B1Checkout);
        Assert.Equal(ArrowEventTypes.RunStarted, shown[0].Type);

        Assert.Equal(BlockOutcomeKind.Ok, result.Outcome.Outcome);
        Assert.Equal("com.shop.Order", result.Analysis!.Cut!.Fqn);
        Assert.Contains("warn-from-analysis", result.Warnings);
        Assert.True(analyzer.CleanedUp);
        Assert.False(analyzer.CleanedUpKeep);
        Assert.True(factory.Disposed);
        Assert.Equal(machine.Environment["JAVA_HOME_17_X64"], Path.GetDirectoryName(Path.GetDirectoryName(factory.Setup!.JavaExecutable)));

        var store = new RunStore(machine.Home);
        var run = store.ReadJson<RunInfo>(store.Open(result.RunId), "run.json")!;
        Assert.Equal((RunStatus.Completed, "1.2.3"), (run.Status, run.ArrowVersion));
        Assert.Equal(["inspect", "Order"], run.Command);
        Assert.Null(run.Config);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal((ProjectSourceKind.LocalDirectory, "MyShop", "abc", false, "sha256:1"), (run.Project.Kind, run.Project.PathName, run.Project.GitCommit, run.Project.Dirty, run.Project.SourceTreeHash));
        Assert.Equal(["com.shop.Order"], run.Targets.Select(t => t.CutFqn));
        Assert.Contains("B1.Checkout", store.ReadText(store.Open(result.RunId), "events.jsonl")!, StringComparison.Ordinal);
        Assert.Contains(RunStore.EventsFile, Directory.GetFiles(result.RunDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task KeepWorkspacesIsPassedToCleanup()
    {
        using var machine = WithJdk17();
        File.WriteAllText(machine.Home.ConfigFile, "{ \"schema_version\": 1, \"run\": { \"keep_workspaces\": true } }");
        var (useCase, _, analyzer) = Create(machine);
        await Run(useCase, Local(machine));
        Assert.True(analyzer.CleanedUpKeep);
    }

    // P07 §2: a failure in any of the first steps creates no run.
    [Fact]
    public async Task PrechecksFailBeforeAnyRunIsCreated()
    {
        using var machine = WithJdk17();
        var (useCase, factory, _) = Create(machine);

        async Task<string> CodeOf(InspectUseCase uc, ProjectTarget target) => (await Assert.ThrowsAsync<ArrowException>(() => Run(uc, target))).Code;

        Assert.Equal(ErrorCodes.B1UrlHasCredentials, await CodeOf(useCase, new ProjectTarget(ProjectSourceKind.GitRepository, "https://u:tok@github.com/a/b", null, "X")));
        Assert.Equal(ErrorCodes.CliArgs, await CodeOf(useCase, new ProjectTarget(ProjectSourceKind.GitRepository, "http://github.com/a/b", null, "X")));
        // --repo needs git.
        Assert.Equal(ErrorCodes.B1CloneFailed, await CodeOf(useCase, new ProjectTarget(ProjectSourceKind.GitRepository, "https://github.com/a/b", null, "X")));

        File.WriteAllText(machine.Home.ConfigFile, "{ \"schema_version\": 1, \"verification\": { \"baseline_runs\": 0 } }");
        Assert.Equal(ErrorCodes.CfgInvalid, await CodeOf(useCase, Local(machine)));
        File.Delete(machine.Home.ConfigFile);

        var (lowDisk, _, _) = Create(machine, free: _ => 1L << 30);
        Assert.Equal(ErrorCodes.CfgDiskFull, await CodeOf(lowDisk, Local(machine)));

        Assert.Empty(Runs(machine));
        Assert.Null(factory.Setup);
    }

    [Fact]
    public async Task NeedsABuildJdkAndAJdkForJavaHelper()
    {
        using var none = new FakeMachine();
        var (noJdk, _, _) = Create(none);
        Assert.Equal(ErrorCodes.B1JdkMissing, (await Assert.ThrowsAsync<ArrowException>(() => Run(noJdk, Local(none)))).Code);

        using var old = new FakeMachine();
        old.AddJdk(8, "1.8.0_422");
        var (onlyOld, _, _) = Create(old);
        Assert.Equal(ErrorCodes.JhJdkMissing, (await Assert.ThrowsAsync<ArrowException>(() => Run(onlyOld, Local(old)))).Code);
        Assert.Empty(Runs(old));
    }

    [Fact]
    public async Task JavaHelperThatDoesNotStartStopsBeforeTheRun()
    {
        using var machine = WithJdk17();
        var (useCase, factory, _) = Create(machine);
        factory.StartFails = new ArrowException(ErrorCodes.JhStartFailed);
        Assert.Equal(ErrorCodes.JhStartFailed, (await Assert.ThrowsAsync<ArrowException>(() => Run(useCase, Local(machine)))).Code);
        Assert.Empty(Runs(machine));
    }

    [Fact]
    public async Task ProjectProblemsMarkTheRunFailedAndCleanUp()
    {
        using var machine = WithJdk17();
        var (useCase, _, analyzer) = Create(machine);
        analyzer.PrepareFails = new ArrowException(ErrorCodes.B1BuildRootNotFound);

        Assert.Equal(ErrorCodes.B1BuildRootNotFound, (await Assert.ThrowsAsync<ArrowException>(() => Run(useCase, Local(machine)))).Code);

        var store = new RunStore(machine.Home);
        var dir = Assert.Single(Runs(machine));
        Assert.Equal(RunStatus.Failed, store.ReadJson<RunInfo>(store.Open(Path.GetFileName(dir)), "run.json")!.Status);
        Assert.False(Directory.Exists(Path.Combine(machine.Home.WorkDir, Path.GetFileName(dir))));
    }

    [Fact]
    public async Task CancellationLeavesACancelledRun()
    {
        using var machine = WithJdk17();
        var (useCase, _, analyzer) = Create(machine);
        analyzer.AnalyzeFails = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(useCase, Local(machine)));

        var store = new RunStore(machine.Home);
        var run = store.ReadJson<RunInfo>(store.Open(Path.GetFileName(Assert.Single(Runs(machine)))), "run.json")!;
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.True(analyzer.CleanedUp);
    }

    // P01 §3.3 step 4: a missing class is BLOCKED with suggestions; the result is returned, not thrown.
    [Fact]
    public async Task MissingAndUntestableClassesAreBlocked()
    {
        using var machine = WithJdk17();
        var (useCase, _, analyzer) = Create(machine);

        analyzer.Locate = (_, _) => new LocateResult.Failed(ErrorCodes.B1CutMissing, "không có class Ordr", ["com.shop.Order"]);
        var missing = await Run(useCase, Local(machine, "Ordr"));
        Assert.Equal((BlockOutcomeKind.Blocked, ErrorCodes.B1CutMissing), (missing.Outcome.Outcome, missing.Outcome.Code));
        Assert.Equal(["com.shop.Order"], missing.Suggestions);
        Assert.Null(missing.Analysis);
        Assert.NotNull(missing.Outcome.Hint);

        analyzer.Locate = (_, _) => new LocateResult.Failed(ErrorCodes.B1CutNotTestable, "com.shop.Repo là interface", []);
        Assert.Equal(ErrorCodes.B1CutNotTestable, (await Run(useCase, Local(machine, "Repo"))).Outcome.Code);

        var store = new RunStore(machine.Home);
        Assert.All(Runs(machine), d => Assert.Equal(RunStatus.Completed, store.ReadJson<RunInfo>(store.Open(Path.GetFileName(d)), "run.json")!.Status));
    }

    [Fact]
    public async Task AmbiguousNamesAskTheUser()
    {
        using var machine = WithJdk17();
        var (useCase, _, analyzer) = Create(machine);
        ClassCandidate[] both = [Class("com.a.Invoice"), Class("com.b.Invoice")];
        analyzer.Locate = (_, _) => new LocateResult.Ambiguous(both);

        var picked = new FakeConfirmation(1);
        var result = await Run(useCase, Local(machine, "Invoice"), picked);
        Assert.Equal("com.b.Invoice", result.Analysis!.Cut!.Fqn);
        Assert.Equal(2, Assert.Single(picked.Asked).Count);
        Assert.Contains("com.a.Invoice", picked.Asked[0][0], StringComparison.Ordinal);

        // No keyboard, or the user gave up: B1_CUT_AMBIGUOUS, naming the candidates.
        var none = await Run(useCase, Local(machine, "Invoice"), new NoConfirmation());
        Assert.Equal((BlockOutcomeKind.Blocked, ErrorCodes.B1CutAmbiguous), (none.Outcome.Outcome, none.Outcome.Code));
        Assert.Contains("com.a.Invoice, com.b.Invoice", none.Outcome.Message, StringComparison.Ordinal);

        // A picked interface is still refused.
        analyzer.Locate = (_, _) => new LocateResult.Ambiguous([Class("com.a.Repo", CutKind.Interface), Class("com.b.Repo")]);
        Assert.Equal(ErrorCodes.B1CutNotTestable, (await Run(useCase, Local(machine, "Repo"), new FakeConfirmation(0))).Outcome.Code);
    }

    [Fact]
    public async Task AbstractClassesRunWithAWarning()
    {
        using var machine = WithJdk17();
        var (useCase, _, analyzer) = Create(machine);
        analyzer.Locate = (_, _) => new LocateResult.Found(Class("com.shop.Base"), "Base là class abstract, khó test trực tiếp");
        var result = await Run(useCase, Local(machine, "Base"));
        Assert.Equal("Base là class abstract, khó test trực tiếp", result.Warnings[0]);
    }
}
