using Arrow.Business.Analysis;
using Arrow.Business.Apply;
using Arrow.Business.Config;
using Arrow.Business.Environment;
using Arrow.Business.Generation;
using Arrow.Business.Llm;
using Arrow.Business.Quality;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.DataAccess.Apply;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.UseCases;

/// <summary>Options of <c>arrow gen</c> (P07 §1, P08 §2.2).</summary>
/// <param name="Model"><c>--model</c>; null for <c>llm.default_model</c>.</param>
/// <param name="Rounds"><c>--rounds</c>; null for <c>repair.rounds</c>.</param>
/// <param name="NoRepair"><c>--no-repair</c>.</param>
/// <param name="NoMetrics"><c>--no-metrics</c>.</param>
/// <param name="Yes"><c>--yes</c>: accept the cost estimate and write the files without asking.</param>
/// <param name="DryRun"><c>--dry-run</c>: never write into the project.</param>
/// <param name="MaxTokens"><c>--max-tokens</c>.</param>
/// <param name="Jobs"><c>--jobs</c>; null for <c>run.jobs</c>.</param>
/// <param name="ResumeRunId"><c>--resume</c>: the run to resume (P07 §5).</param>
public sealed record GenerateOptions(string? Model, int? Rounds, bool NoRepair, bool NoMetrics, bool Yes, bool DryRun, long? MaxTokens, int? Jobs = null, string? ResumeRunId = null);

/// <summary>A request of <c>arrow gen</c> (P07 §1).</summary>
/// <param name="Kind">Local folder or GitHub repository.</param>
/// <param name="Location">Folder or URL.</param>
/// <param name="GitRef"><c>--ref</c>.</param>
/// <param name="CutQueries">Class names as typed; may be empty with <paramref name="Package"/>.</param>
/// <param name="Package"><c>--package</c>.</param>
/// <param name="Options">The options.</param>
public sealed record GenerateRequest(ProjectSourceKind Kind, string Location, string? GitRef, IReadOnlyList<string> CutQueries, string? Package, GenerateOptions Options);

/// <summary>What <c>arrow gen</c> did (P07 §9).</summary>
/// <param name="RunId">The run.</param>
/// <param name="RunDir">Its folder.</param>
/// <param name="Status">Status of the run.</param>
/// <param name="ExitCode">Exit code of P07 §9.</param>
/// <param name="Targets">The <c>result.json</c> of each CUT, in the order of the request.</param>
/// <param name="Totals">Model calls, tokens and cost of the run.</param>
/// <param name="Warnings">Warnings to print.</param>
public sealed record GenerateResult(string RunId, string RunDir, RunStatus Status, int ExitCode, IReadOnlyList<TargetResult> Targets, RunTotals Totals, IReadOnlyList<string> Warnings);

/// <summary><c>arrow gen</c> (P07).</summary>
public interface IGenerateTestsUseCase
{
    /// <summary>
    /// Generates, verifies, repairs and measures a test for each class of <paramref name="request"/>, then offers to write them.
    /// Environment and configuration problems raise <see cref="ArrowException"/> before a run exists (exit 2); Ctrl+C raises
    /// <see cref="OperationCanceledException"/> after the run is marked CANCELLED.
    /// </summary>
    Task<GenerateResult> RunAsync(GenerateRequest request, IReadOnlyList<string> command, string? projectRoot, IProgress<ArrowEvent>? progress, IConfirmation confirm, CancellationToken ct);
}

/// <summary>
/// <see cref="IGenerateTestsUseCase"/> (P07): checks, the run, the classes, the cost question, the targets in parallel (at most
/// <c>--jobs</c>, sharing block 1 per module and one model client), then the write step and the exit code.
/// </summary>
public sealed class GenerateTestsUseCase(
    IConfigStore config,
    ArrowHome home,
    ToolDiscovery tools,
    IRunStore runs,
    IAnalyzerHostFactory hosts,
    ILlmClientFactory llms,
    IProjectFileWriter writer,
    IToolStore toolStore,
    IProcessRunner processes,
    string arrowVersion,
    Func<string, long?>? freeBytes = null,
    ILlmCostEstimator? estimator = null) : IGenerateTestsUseCase
{
    /// <summary>Exit codes of P07 §9.</summary>
    public const int ExitValid = 0, ExitNotValid = 1, ExitBlocked = 2, ExitCancelled = 3;

    /// <summary>Characters counted for the block of M in the estimate (P07 §4).</summary>
    public const int MetadataChars = 1500;

    private static readonly Dictionary<string, string?> NoOverrides = [];

    public async Task<GenerateResult> RunAsync(GenerateRequest request, IReadOnlyList<string> command, string? projectRoot, IProgress<ArrowEvent>? progress, IConfirmation confirm, CancellationToken ct)
    {
        var options = request.Options;

        // P07 §2: nothing is created until these pass.
        if (request.CutQueries.Count == 0 && request.Package is null && options.ResumeRunId is null)
        {
            throw new ArrowException(ErrorCodes.CliArgs, "Cần tên class hoặc --package.");
        }

        var remote = request.Kind == ProjectSourceKind.GitRepository ? GitRemote.Parse(request.Location) : null;
        var effective = config.Load(projectRoot, NoOverrides);
        ConfigValidator.ThrowIfInvalid(effective);
        var settings = effective.Config;
        if (options.Rounds is < 1 or > 10)
        {
            throw new ArrowException(ErrorCodes.CliArgs, "--rounds phải từ 1 đến 10.");
        }

        var jobs = options.Jobs ?? settings.Run.Jobs;
        if (jobs is < 1 or > 8)
        {
            throw new ArrowException(ErrorCodes.CliArgs, "--jobs phải từ 1 đến 8.");
        }

        var catalog = ErrorCatalog.For(settings.Ui.Language);
        var warnings = new List<string>();

        // P07 §5.1: a run to resume, named or found; its settings win over the options (P07 §5.2).
        var resume = await FindResumeAsync(request, confirm, ct).ConfigureAwait(false);
        if (resume?.Info.Config is { } old)
        {
            var changed = new List<string>();
            if (options.Model is { } m && m != old.Model) { changed.Add($"model {old.Model}"); }
            if (options.Rounds is { } n && n != old.Rounds) { changed.Add($"N = {old.Rounds}"); }
            if (options.NoRepair == old.Repair) { changed.Add(old.Repair ? "có sửa" : "không sửa"); }
            if (options.NoMetrics == old.Quality) { changed.Add(old.Quality ? "có đo" : "không đo"); }
            if (changed.Count > 0)
            {
                warnings.Add("Dùng cấu hình của run cũ: " + string.Join(", ", changed));
            }

            options = options with { Model = old.Model, Rounds = old.Rounds, NoRepair = !old.Repair, NoMetrics = !old.Quality };
            settings = settings with { Repair = settings.Repair with { EarlyStopP = old.EarlyStopP } };
        }

        var model = ResolvedModel.From(settings, options.Model);
        var limits = LlmLimits.From(settings, options.MaxTokens);
        _ = llms.CreateProvider(model, limits); // CFG_SECRET_MISSING before anything starts
        var templates = new PromptTemplateStore(settings.Prompts.OverrideDir);

        var machine = await tools.FindAsync(settings.Java, ct).ConfigureAwait(false);
        if (remote is not null && machine.Git is null)
        {
            throw new ArrowException(ErrorCodes.B1CloneFailed, "Máy không có git, nên không tải được repo. Cài git hoặc đặt java.git_path.");
        }

        if (machine.BuildJdkHomes.Count == 0)
        {
            throw new ArrowException(ErrorCodes.B1JdkMissing, args: new Dictionary<string, string> { ["jdk"] = "17" });
        }

        var java = JavaHelperJar.FindJava(machine.HelperJdkHomes);
        if (home.Validate() is { } invalidHome)
        {
            throw invalidHome;
        }

        home.EnsureCreated();
        if ((freeBytes ?? DiskSpace.FreeBytes)(home.Root) is { } free && free < InspectUseCase.MinimumFreeBytes)
        {
            throw new ArrowException(ErrorCodes.CfgDiskFull);
        }

        var analysisOptions = AnalysisOptions.From(settings.Verification, machine.BuildJdkHomes, machine.Maven, machine.Gradle);
        await using var host = await hosts.StartAsync(new AnalyzerSetup(java, machine.Git, analysisOptions, catalog, jobs), ct).ConfigureAwait(false);

        // P07 §3: the run (a resumed one keeps its id).
        var id = resume?.Handle.RunId ?? runs.NewUniqueRunId(DateTime.Now);
        var repair = settings.Repair with { Enabled = settings.Repair.Enabled && !options.NoRepair, Rounds = options.Rounds ?? settings.Repair.Rounds };
        var measure = !options.NoMetrics && settings.Quality.Enabled
            ? new QualityOptions(settings.Quality.Tools, machine.BuildJdkHomes, java, TimeSpan.FromSeconds(settings.Verification.StepTimeoutSeconds),
                TimeSpan.FromSeconds(settings.Quality.PitTimeoutSeconds), TimeSpan.FromSeconds(settings.Quality.TsdetectTimeoutSeconds),
                settings.Verification.InfraAttempts, TimeSpan.FromSeconds(settings.Verification.InfraWaitSeconds), settings.Run.KeepWorkspaces)
            : null;
        var project = new RunProject(request.Kind, remote?.RepoPath ?? new DirectoryInfo(request.Location).Name,
            remote is null ? request.Location.Replace('\\', '/') : null, remote?.Url, request.GitRef, null, null, null);
        var info = resume is { } r
            ? r.Info with { Status = RunStatus.Running, FinishedAt = null }
            : new RunInfo(id, arrowVersion, DateTimeOffset.UtcNow, null, RunStatus.Running, command, project,
                new RunConfig(model.Name, model.BaseUrl.Host, repair.Rounds, repair.EarlyStopP, repair.Enabled, measure is not null),
                templates.All().ToDictionary(t => t.Path, t => t.Sha256), new Dictionary<string, string>(), [], RunTotals.Zero);
        var handle = resume?.Handle ?? runs.Create(info);
        if (resume is not null)
        {
            runs.Update(handle, info);
        }
        var work = Path.Combine(home.WorkDir, id);
        if (Directory.Exists(work))
        {
            // GĐ7: a run killed before its cleanup leaves work/<run> behind, and resuming it then failed on "git worktree add" into the old
            // base/. Nothing in it is kept across runs (block 1 runs again on resume), so it starts empty.
            WorkspaceManager.DeleteTree(work);
        }

        Directory.CreateDirectory(work);
        var run = new RunContext(id, home.Root, work, handle, new RunProgress(runs, handle, progress));
        Report(run, ArrowEventTypes.RunStarted, null, "arrow " + string.Join(' ', command));

        var state = new RunState(runs, handle, info);
        ProjectWorkspace? workspace = null;
        var results = new Dictionary<string, TargetResult>(StringComparer.Ordinal);
        IReadOnlyList<string> order = [];
        try
        {
            var target = new ProjectTarget(request.Kind, request.Location, resume?.Info.Project.GitCommit ?? request.GitRef, request.CutQueries.FirstOrDefault() ?? "");
            workspace = await host.Analyzer.PrepareAsync(target, run, size => confirm.ConfirmAsync(
                $"Project nặng {size / (1024 * 1024 * 1024.0):0.0} GB sau khi lọc. Vẫn chép?", true, ct), ct).ConfigureAwait(false);
            if (resume is { } previous && !SameCode(previous.Info.Project, workspace.Checkout, WrittenPaths(handle, previous.Info.Targets)))
            {
                throw new ArrowException(ErrorCodes.GenResumeMismatch, args: new Dictionary<string, string> { ["run_id"] = id });
            }

            state.Update(i => i with
            {
                Project = (resume?.Info.Project ?? project) with
                {
                    Root = workspace.Checkout.ProjectRoot?.Replace('\\', '/') ?? project.Root,
                    GitCommit = workspace.Checkout.GitCommit,
                    Dirty = workspace.Checkout.Dirty,
                    SourceTreeHash = workspace.Checkout.SourceTreeHash,
                },
            });
            warnings.AddRange(workspace.Warnings);

            // P07 §3 steps 4–6: the classes; a resumed run keeps its finished targets (P07 §5.3).
            var kept = new List<TargetRun>();
            var selection = request;
            if (resume is not null)
            {
                var (redo, keep) = SplitForResume(handle, state.Info.Targets);
                foreach (var done in keep)
                {
                    results[done.Result.CutFqn] = done.Result;
                    if (!runs.Exists(handle, $"{ProjectAnalyzer.TargetDir(done.Result.CutFqn)}/apply.json") && done.Final is not null)
                    {
                        kept.Add(done);
                    }
                }

                selection = request with { CutQueries = redo, Package = null };
                warnings.Add($"Chạy tiếp run {id}: giữ {keep.Count} class, chạy lại {redo.Count} class");
            }

            var (cuts, blocked) = await SelectAsync(host.Analyzer, workspace, selection, catalog, confirm, warnings, ct).ConfigureAwait(false);
            order = (resume is null ? [] : state.Info.Targets.Select(t => t.CutFqn).ToList())
                .Concat(cuts.Select(c => c.Fqn)).Concat(blocked.Select(b => b.CutFqn)).Distinct(StringComparer.Ordinal).ToList();
            state.Update(i => i with
            {
                Targets = order.Select(fqn => new RunTarget(fqn,
                    results.ContainsKey(fqn) || blocked.Any(b => b.CutFqn == fqn) ? TargetState.Done : TargetState.Pending)).ToList(),
            });
            foreach (var b in blocked)
            {
                runs.WriteJson(handle, $"{ProjectAnalyzer.TargetDir(b.CutFqn)}/result.json", b);
                results[b.CutFqn] = b;
            }

            Report(run, ArrowEventTypes.RunTargetsResolved, null, $"{cuts.Count} class: {string.Join(", ", cuts.Select(c => c.SimpleName))}", ("targets", cuts.Select(c => c.Fqn).ToList()));

            // P07 §4: the estimate and the question.
            if (cuts.Count > 0 && !await ConfirmCostAsync(cuts, workspace, model, repair, templates, limits, settings, options, confirm, run, ct).ConfigureAwait(false))
            {
                state.Update(i => i with { Status = RunStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow });
                Finish(state, project, order, results);
                return new GenerateResult(id, handle.Root, RunStatus.Cancelled, ExitCancelled, Ordered(order, results), RunTotals.Zero, warnings);
            }

            // P07 §6–7: the targets, at most `jobs` at a time.
            var shared = new PipelineShared(host, llms.Create(model, handle, limits, run.Progress), model, templates, catalog.With("run_id", id).With("log_path", handle.Root), settings, repair, measure, workspace, runs, toolStore, processes);
            var runs2 = await RunTargetsAsync(cuts, run, shared, jobs, state, results, warnings, ct).ConfigureAwait(false);

            // P07 §9: the write step, then the end of the run.
            var policy = new TestWritePolicy(writer, host.Verifier, host.JavaHelpers, runs, catalog);
            var applyContext = new ApplyContext(run, workspace.Checkout.ProjectRoot, options.Yes, options.DryRun, settings.Apply.Confirm,
                TimeSpan.FromSeconds(settings.Verification.StepTimeoutSeconds), settings.Verification.InfraAttempts, TimeSpan.FromSeconds(settings.Verification.InfraWaitSeconds));
            var applied = await ApplyStep.RunAsync(policy, [.. kept, .. runs2], applyContext, confirm, warnings, ct).ConfigureAwait(false);
            foreach (var (cut, apply) in applied)
            {
                var withApply = results[cut] with { Apply = apply };
                results[cut] = withApply;
                runs.WriteJson(handle, $"{ProjectAnalyzer.TargetDir(cut)}/result.json", withApply);
            }

            var stopped = runs2.Any(r => r.Result is { Result: TargetOutcome.Cancelled, Error: not null });
            var ordered = Ordered(order, results);
            var totals = new RunTotals(ordered.Sum(r => r.LlmCalls), ordered.Sum(r => r.Tokens.In), ordered.Sum(r => r.Tokens.Out),
                ordered.Any(r => r.CostUsd is not null) ? ordered.Sum(r => r.CostUsd ?? 0) : null);
            state.Update(i => i with { Status = stopped ? RunStatus.Failed : RunStatus.Completed, FinishedAt = DateTimeOffset.UtcNow, Totals = totals });
            Finish(state, project, order, results);
            Report(run, ArrowEventTypes.RunFinished, null,
                $"Run {id}: {ordered.Count(r => r.Result == TargetOutcome.Valid)}/{ordered.Count} VALID", ("valid", ordered.Count(r => r.Result == TargetOutcome.Valid)), ("targets", ordered.Count));
            return new GenerateResult(id, handle.Root, state.Info.Status, ExitCodeOf(ordered, stopped), ordered, totals, warnings);
        }
        catch (OperationCanceledException)
        {
            // P07 §8: whatever ends the run, run.json and history.jsonl record it.
            state.Update(i => i with { Status = RunStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow });
            Finish(state, state.Info.Project, order.Count > 0 ? order : state.Info.Targets.Select(t => t.CutFqn).ToList(), results);
            throw;
        }
        catch (Exception)
        {
            state.Update(i => i with { Status = RunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow });
            Finish(state, state.Info.Project, order.Count > 0 ? order : state.Info.Targets.Select(t => t.CutFqn).ToList(), results);
            throw;
        }
        finally
        {
            if (workspace is not null)
            {
                await host.Analyzer.CleanupAsync(workspace, run, settings.Run.KeepWorkspaces, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                WorkspaceManager.DeleteTree(work);
            }
        }
    }

    /// <summary>
    /// P07 §5.1: the run of <c>--resume</c>, or the newest unfinished <c>gen</c> run of the same project from the last 7 days, when
    /// the user agrees to resume it (<c>--yes</c> agrees; without a keyboard a new run starts). Null for a new run.
    /// </summary>
    private async Task<(RunHandle Handle, RunInfo Info)?> FindResumeAsync(GenerateRequest request, IConfirmation confirm, CancellationToken ct)
    {
        if (request.Options.ResumeRunId is { } named)
        {
            var handle = runs.Open(named);
            var info = runs.ReadJson<RunInfo>(handle, "run.json");
            if (info is null || info.Command.FirstOrDefault() != "gen" || !SameProject(info.Project, request))
            {
                throw new ArrowException(ErrorCodes.GenResumeNotFound, args: new Dictionary<string, string> { ["run_id"] = named });
            }

            return (handle, info);
        }

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var candidate = runs.Recent(50).FirstOrDefault(r =>
            r.Info.Status is RunStatus.Running or RunStatus.Cancelled or RunStatus.Failed
            && r.Info.StartedAt >= since
            && r.Info.Command.FirstOrDefault() == "gen"
            && SameProject(r.Info.Project, request));
        if (candidate.Info is null)
        {
            return null;
        }

        var targets = candidate.Info.Targets;
        var question = $"Run {candidate.Info.RunId} chưa xong ({targets.Count(t => t.State == TargetState.Done)}/{targets.Count} class). Chạy tiếp?";
        var yes = request.Options.Yes || (confirm.CanAsk && await confirm.ConfirmAsync(question, true, ct).ConfigureAwait(false));
        return yes ? candidate : null;
    }

    /// <summary>The same local folder, or the same repository URL.</summary>
    private static bool SameProject(RunProject project, GenerateRequest request) =>
        request.Kind == ProjectSourceKind.GitRepository
            ? project.Url is not null && GitRemote.Parse(request.Location).Url == project.Url
            : project.Root is not null && string.Equals(
                project.Root.Replace('\\', '/').TrimEnd('/'), Path.GetFullPath(request.Location).Replace('\\', '/').TrimEnd('/'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// P07 §5.2: a folder must have the same <c>source_tree_hash</c>, without the test files this run wrote into it; a repository the
    /// same commit.
    /// </summary>
    private static bool SameCode(RunProject before, SourceCheckout now, IReadOnlySet<string> written)
    {
        if (before.SourceTreeHash is null)
        {
            return before.GitCommit == now.GitCommit;
        }

        return now.ProjectRoot is { } root && LocalProjectTree.Scan(root).SourceTreeHashWithout(written) == before.SourceTreeHash;
    }

    /// <summary>Paths that this run wrote into the project (<c>apply.json</c> with <c>written</c>).</summary>
    private HashSet<string> WrittenPaths(RunHandle handle, IReadOnlyList<RunTarget> targets) =>
        targets.Select(t => runs.ReadJson<ApplyRecord>(handle, $"{ProjectAnalyzer.TargetDir(t.CutFqn)}/apply.json"))
            .Where(a => a is { Decision: ApplyDecision.Written, Path: not null })
            .Select(a => a!.Path!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// P07 §5.3: DONE targets other than BLOCKED and INFRA are kept (with what the write step needs when they have no
    /// <c>apply.json</c>); the others run again from the start, their old files removed.
    /// </summary>
    private (List<string> Redo, List<TargetRun> Keep) SplitForResume(RunHandle handle, IReadOnlyList<RunTarget> targets)
    {
        var redo = new List<string>();
        var keep = new List<TargetRun>();
        foreach (var t in targets)
        {
            var dir = ProjectAnalyzer.TargetDir(t.CutFqn);
            var result = runs.ReadJson<TargetResult>(handle, $"{dir}/result.json");
            if (t.State == TargetState.Done && result is not null && result.Result is not (TargetOutcome.Blocked or TargetOutcome.Infra))
            {
                keep.Add(new TargetRun(result, KeptFinal(handle, dir, result), runs.ReadJson<RepositoryMetadata>(handle, $"{dir}/block1/metadata.json"), []));
                continue;
            }

            foreach (var part in new[] { "block1", "block2", "block3", "block4", "apply", "result.json", "apply.json" })
            {
                runs.Delete(handle, $"{dir}/{part}");
            }

            redo.Add(t.CutFqn);
        }

        return (redo, keep);
    }

    /// <summary>The final suite of a kept target, rebuilt from <c>final.json</c> and <c>final.java</c>.</summary>
    private FinalTestSuite? KeptFinal(RunHandle handle, string dir, TargetResult result)
    {
        var final = runs.ReadJson<FinalTestSuite>(handle, $"{dir}/block3/final.json");
        var code = runs.ReadText(handle, $"{dir}/block3/final.java");
        if (final is null || code is null || result.TestFqn is null || result.TestPath is null)
        {
            return null;
        }

        var candidate = new Common.Generation.CandidateTest(true, null, null, code, result.TestFqn, result.TestPath, null, 0, 0, [], [], null);
        return final with { Candidate = candidate, Verify = new Common.Verification.VerifyResult { State = final.FinalState, GeneratedTestRanInModule = final.GeneratedTestRanInModule } };
    }

    /// <summary>
    /// The classes of the queries and the package (P07 §3 steps 4–5), without repeats, in the order typed. A query without a usable
    /// class becomes a BLOCKED target; the other targets still run.
    /// </summary>
    private static async Task<(List<ClassCandidate> Cuts, List<TargetResult> Blocked)> SelectAsync(
        IProjectAnalyzer analyzer, ProjectWorkspace workspace, GenerateRequest request, ErrorCatalog catalog, IConfirmation confirm, List<string> warnings, CancellationToken ct)
    {
        var cuts = new List<ClassCandidate>();
        var blocked = new List<TargetResult>();
        foreach (var query in request.CutQueries)
        {
            var picked = await CutPicker.PickAsync(analyzer, workspace, query, catalog, confirm, ct).ConfigureAwait(false);
            if (picked.Warning is not null)
            {
                warnings.Add(picked.Warning);
            }

            if (picked.Cut is null)
            {
                blocked.Add(TargetPipeline.Blocked(query, picked.Blocked!, 0));
            }
            else if (cuts.All(c => c.Fqn != picked.Cut.Fqn))
            {
                cuts.Add(picked.Cut);
            }
        }

        if (request.Package is { } package)
        {
            var inPackage = ClassLocator.InPackage(workspace.Classes, package);
            if (inPackage.Count == 0)
            {
                var info = catalog.Describe(ErrorCodes.B1CutMissing, $"Package {package} không có class cụ thể nào.", new Dictionary<string, string> { ["query"] = package });
                blocked.Add(TargetPipeline.Blocked(package, new BlockOutcome(BlockOutcomeKind.Blocked, ErrorCodes.B1CutMissing, info.Message, info.Hint, null, null, 0), 0));
            }

            cuts.AddRange(inPackage.Where(c => cuts.All(x => x.Fqn != c.Fqn)));
        }

        return (cuts, blocked);
    }

    /// <summary>
    /// P07 §4: the estimate, asked when there are at least <c>run.confirm_cost_from_classes</c> classes or the maximum is over
    /// <c>--max-tokens</c>. <c>--yes</c> accepts; without a keyboard the answer is no.
    /// </summary>
    private async Task<bool> ConfirmCostAsync(
        IReadOnlyList<ClassCandidate> cuts, ProjectWorkspace workspace, ResolvedModel model, Common.Config.RepairSettings repair, IPromptTemplateStore templates,
        LlmLimits limits, Common.Config.ArrowConfig settings, GenerateOptions options, IConfirmation confirm, RunContext run, CancellationToken ct)
    {
        var templateChars = templates.Get("generation/system/zero_shot_project_aware.md").Body.Length + templates.Get("generation/user/project_aware.md").Body.Length;
        var sizes = cuts.Select(c =>
        {
            var file = Path.Combine(workspace.Checkout.Root, c.Path.Replace('/', Path.DirectorySeparatorChar));
            var chars = File.Exists(file) ? (int)Math.Min(new FileInfo(file).Length, TestGenerator.MaxCutChars) : TestGenerator.MaxCutChars;
            return new PromptSizeHint(chars + templateChars + MetadataChars);
        }).ToList();
        var estimate = (estimator ?? new LlmCostEstimator()).Estimate(model, sizes, repair.Enabled ? repair.Rounds : 0);
        var text = EstimateText(cuts.Count, model.Name, repair.Rounds, estimate);
        Report(run, ArrowEventTypes.RunEstimate, null, text, ("expected_tokens", estimate.ExpectedTokens), ("max_tokens", estimate.MaxTokens),
            ("expected_cost_usd", estimate.ExpectedCostUsd), ("max_cost_usd", estimate.MaxCostUsd));

        var ask = cuts.Count >= settings.Run.ConfirmCostFromClasses || (limits.MaxTokens is { } budget && estimate.MaxTokens > budget);
        if (!ask || options.Yes)
        {
            return true;
        }

        if (!confirm.CanAsk)
        {
            Report(run, ArrowEventTypes.RunStopping, null, "Bỏ qua: cần --yes khi chạy không tương tác", level: EventLevel.Warning);
            return false;
        }

        return await confirm.ConfirmAsync(text + " Tiếp tục?", true, ct).ConfigureAwait(false);
    }

    /// <summary><c>Ước tính cho 8 class (gpt-x, N=5): ~45k token (~0.03 USD), tối đa ~120k token (~0.08 USD).</c></summary>
    public static string EstimateText(int classes, string model, int rounds, CostEstimate estimate)
    {
        static string Tokens(long n) => n >= 1000 ? $"~{n / 1000.0:0}k token" : $"~{n} token";
        static string Usd(double? cost) => cost is { } c ? $" (~{c.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} USD)" : "";
        return $"Ước tính cho {classes} class ({model}, N={rounds}): {Tokens(estimate.ExpectedTokens)}{Usd(estimate.ExpectedCostUsd)}, tối đa {Tokens(estimate.MaxTokens)}{Usd(estimate.MaxCostUsd)}.";
    }

    /// <summary>Runs the targets, at most <paramref name="jobs"/> at once, starting them in order (P07 §7). A stop of the run (P03) starts no new target.</summary>
    private static async Task<IReadOnlyList<TargetRun>> RunTargetsAsync(
        IReadOnlyList<ClassCandidate> cuts, RunContext run, PipelineShared shared, int jobs, RunState state, Dictionary<string, TargetResult> results, List<string> warnings, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(jobs);
        var done = new TargetRun?[cuts.Count];
        var stop = 0;
        var tasks = new List<Task>();
        for (var i = 0; i < cuts.Count; i++)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            if (Volatile.Read(ref stop) == 1)
            {
                gate.Release();
                break;
            }

            var index = i;
            var cut = cuts[i];
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var targetRun = run with { Progress = new TargetProgress(run.Progress, cut.Fqn, index + 1, cuts.Count) };
                    state.SetTarget(cut.Fqn, TargetState.Running);
                    Report(targetRun, ArrowEventTypes.RunTargetStarted, cut.Fqn, $"[{index + 1}/{cuts.Count}] {cut.Fqn}");
                    TargetRun result;
                    try
                    {
                        result = await TargetPipeline.RunAsync(cut, targetRun, shared, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // P07 §8: an unexpected error ends this target only.
                        var info = shared.Catalog.Describe(ErrorCodes.CliInternal, ex.Message);
                        result = new TargetRun(TargetPipeline.Blocked(cut.Fqn, new BlockOutcome(BlockOutcomeKind.Infra, info.Code, info.Message, info.Hint, ex.ToString(), null, 0), 0) with { Error = info }, null, null, []);
                    }

                    done[index] = result;
                    lock (results)
                    {
                        results[cut.Fqn] = result.Result;
                        // With several classes, each warning names its class: a module warning is otherwise unplaceable.
                        warnings.AddRange(cuts.Count == 1 ? result.Warnings : result.Warnings.Select(w => $"{cut.SimpleName}: {w}"));
                    }

                    shared.Runs.WriteJson(run.Run, $"{ProjectAnalyzer.TargetDir(cut.Fqn)}/result.json", result.Result);
                    var stopped = result.Result is { Result: TargetOutcome.Cancelled, Error: not null };
                    if (stopped)
                    {
                        Interlocked.Exchange(ref stop, 1);
                        Report(targetRun, ArrowEventTypes.RunStopping, cut.Fqn, $"{result.Result.Error!.Code}: dừng run, không bắt đầu class mới", level: EventLevel.Error);
                    }

                    // A target stopped by P03 keeps RUNNING so that the run can be resumed (P07 §8).
                    state.SetTarget(cut.Fqn, stopped ? TargetState.Running : TargetState.Done);
                    Report(targetRun, ArrowEventTypes.RunTargetDone, cut.Fqn, $"[{index + 1}/{cuts.Count}] {cut.SimpleName}: {result.Result.Result.ToString().ToUpperInvariant()}",
                        ("result", result.Result.Result.ToString().ToUpperInvariant()));
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return done.OfType<TargetRun>().ToList();
    }

    /// <summary>Exit code of a run (P07 §9).</summary>
    public static int ExitCodeOf(IReadOnlyList<TargetResult> targets, bool stoppedByLlm)
    {
        if (stoppedByLlm || targets.Count == 0 || targets.All(t => t.Result == TargetOutcome.Blocked))
        {
            return ExitBlocked;
        }

        return targets.All(t => t.Result == TargetOutcome.Valid) ? ExitValid : ExitNotValid;
    }

    private static IReadOnlyList<TargetResult> Ordered(IReadOnlyList<string> order, Dictionary<string, TargetResult> results) =>
        order.Where(results.ContainsKey).Select(fqn => results[fqn]).ToList();

    private void Finish(RunState state, RunProject project, IReadOnlyList<string> order, Dictionary<string, TargetResult> results)
    {
        var ordered = Ordered(order, results);
        runs.AppendHistory(new HistoryEntry(state.Info.RunId, state.Info.StartedAt, project.PathName, order,
            ordered.Count(r => r.Result == TargetOutcome.Valid), ordered.Count(r => r.Apply?.Decision == ApplyDecision.Written), state.Info.Status));
    }

    private static void Report(RunContext run, string type, string? cut, string message, EventLevel level = EventLevel.Info, params (string Key, object? Value)[] data) =>
        run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, cut, null, null, level, message, data.ToDictionary(d => d.Key, d => d.Value)));

    private static void Report(RunContext run, string type, string? cut, string message, params (string Key, object? Value)[] data) =>
        Report(run, type, cut, message, EventLevel.Info, data);

    /// <summary><c>run.json</c>, updated from several targets at once.</summary>
    private sealed class RunState(IRunStore runs, RunHandle handle, RunInfo info)
    {
        private readonly object _gate = new();

        public RunInfo Info { get; private set; } = info;

        public void Update(Func<RunInfo, RunInfo> change)
        {
            lock (_gate)
            {
                Info = change(Info);
                runs.Update(handle, Info);
            }
        }

        public void SetTarget(string cut, TargetState target) =>
            Update(i => i with { Targets = i.Targets.Select(t => t.CutFqn == cut ? t with { State = target } : t).ToList() });
    }

    /// <summary>Adds the position <c>[i/n]</c> to the events of a target (P07 §10).</summary>
    private sealed class TargetProgress(IProgress<ArrowEvent> inner, string cut, int index, int total) : IProgress<ArrowEvent>
    {
        public void Report(ArrowEvent value) =>
            inner.Report(value.Index is null && (value.CutFqn is null || value.CutFqn == cut) ? value with { CutFqn = value.CutFqn ?? cut, Index = index, Total = total } : value);
    }
}
