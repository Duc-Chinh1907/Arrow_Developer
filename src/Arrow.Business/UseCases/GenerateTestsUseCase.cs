using System.Diagnostics;
using System.Security.Cryptography;
using Arrow.Business.Analysis;
using Arrow.Business.Config;
using Arrow.Business.Environment;
using Arrow.Business.Generation;
using Arrow.Business.Llm;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Generation;
using Arrow.Common.Verification;
using Arrow.DataAccess.Apply;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.UseCases;

/// <summary>Options of <c>arrow gen</c> (P07 §1, P08 §2.2) that the minimal version supports.</summary>
/// <param name="Model"><c>--model</c>; null for <c>llm.default_model</c>.</param>
/// <param name="Rounds"><c>--rounds</c>; null for <c>repair.rounds</c>.</param>
/// <param name="NoRepair"><c>--no-repair</c>.</param>
/// <param name="NoMetrics"><c>--no-metrics</c>.</param>
/// <param name="Yes"><c>--yes</c>: write the file without asking.</param>
/// <param name="DryRun"><c>--dry-run</c>: never write into the project.</param>
/// <param name="MaxTokens"><c>--max-tokens</c>.</param>
public sealed record GenerateOptions(string? Model, int? Rounds, bool NoRepair, bool NoMetrics, bool Yes, bool DryRun, long? MaxTokens);

/// <summary>What <c>arrow gen</c> did (P07 §9).</summary>
/// <param name="RunId">The run.</param>
/// <param name="RunDir">Its folder.</param>
/// <param name="Status">Status of the run.</param>
/// <param name="ExitCode">Exit code of P07 §9.</param>
/// <param name="Targets">The <c>result.json</c> of each CUT.</param>
/// <param name="Totals">Model calls, tokens and cost of the run.</param>
/// <param name="Warnings">Warnings to print.</param>
public sealed record GenerateResult(string RunId, string RunDir, RunStatus Status, int ExitCode, IReadOnlyList<TargetResult> Targets, RunTotals Totals, IReadOnlyList<string> Warnings);

/// <summary><c>arrow gen</c> (P07).</summary>
public interface IGenerateTestsUseCase
{
    /// <summary>
    /// Generates, verifies and repairs a test for the class of <paramref name="target"/>, then offers to write it. Environment and
    /// configuration problems raise <see cref="ArrowException"/> before a run exists (exit 2); a cancellation raises
    /// <see cref="OperationCanceledException"/> after the run is marked CANCELLED.
    /// </summary>
    Task<GenerateResult> RunAsync(ProjectTarget target, GenerateOptions options, IReadOnlyList<string> command, string? projectRoot, IProgress<ArrowEvent>? progress, IConfirmation confirm, CancellationToken ct);
}

/// <summary>
/// <see cref="IGenerateTestsUseCase"/>, the minimal version of milestone M1 (PLAN GĐ4): one class, blocks 1 → 2 → 3, no block 4,
/// and a simple writer that never overwrites. Several classes, <c>--jobs</c>, resume, renaming before writing and the cost
/// question come with GĐ6.
/// </summary>
public sealed class GenerateTestsUseCase(
    IConfigStore config,
    ArrowHome home,
    ToolDiscovery tools,
    IRunStore runs,
    IAnalyzerHostFactory hosts,
    ILlmClientFactory llms,
    IProjectFileWriter writer,
    string arrowVersion,
    Func<string, long?>? freeBytes = null) : IGenerateTestsUseCase
{
    /// <summary>Exit codes of P07 §9.</summary>
    public const int ExitValid = 0, ExitNotValid = 1, ExitBlocked = 2, ExitCancelled = 3;

    private static readonly Dictionary<string, string?> NoOverrides = [];

    private IRunStore Runs => runs;

    private ILlmClientFactory Llms => llms;

    private IProjectFileWriter Writer => writer;

    public async Task<GenerateResult> RunAsync(ProjectTarget target, GenerateOptions options, IReadOnlyList<string> command, string? projectRoot, IProgress<ArrowEvent>? progress, IConfirmation confirm, CancellationToken ct)
    {
        // P07 §2: nothing is created until these pass.
        var remote = target.Kind == ProjectSourceKind.GitRepository ? GitRemote.Parse(target.Location) : null;
        var effective = config.Load(projectRoot, NoOverrides);
        ConfigValidator.ThrowIfInvalid(effective);
        var settings = effective.Config;
        if (options.Rounds is < 1 or > 10)
        {
            throw new ArrowException(ErrorCodes.CliArgs, "--rounds phải từ 1 đến 10.");
        }

        var catalog = ErrorCatalog.For(settings.Ui.Language);
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
        await using var host = await hosts.StartAsync(new AnalyzerSetup(java, machine.Git, analysisOptions, catalog), ct).ConfigureAwait(false);

        // P07 §3: the run.
        var id = runs.NewUniqueRunId(DateTime.Now);
        var repair = settings.Repair with { Enabled = settings.Repair.Enabled && !options.NoRepair, Rounds = options.Rounds ?? settings.Repair.Rounds };
        var project = new RunProject(target.Kind, remote?.RepoPath ?? new DirectoryInfo(target.Location).Name,
            remote is null ? target.Location.Replace('\\', '/') : null, remote?.Url, target.GitRef, null, null, null);
        var info = new RunInfo(id, arrowVersion, DateTimeOffset.UtcNow, null, RunStatus.Running, command, project,
            new RunConfig(model.Name, model.BaseUrl.Host, repair.Rounds, repair.EarlyStopP, repair.Enabled, false),
            templates.All().ToDictionary(t => t.Path, t => t.Sha256), new Dictionary<string, string>(), [], RunTotals.Zero);
        var handle = runs.Create(info);
        var work = Path.Combine(home.WorkDir, id);
        Directory.CreateDirectory(work);
        var run = new RunContext(id, home.Root, work, handle, new RunProgress(runs, handle, progress));
        Report(run, ArrowEventTypes.RunStarted, null, "arrow " + string.Join(' ', command));

        ProjectWorkspace? workspace = null;
        var warnings = new List<string>();
        try
        {
            workspace = await host.Analyzer.PrepareAsync(target, run, size => confirm.ConfirmAsync(
                $"Project nặng {size / (1024 * 1024 * 1024.0):0.0} GB sau khi lọc. Vẫn chép?", true, ct), ct).ConfigureAwait(false);
            info = info with
            {
                Project = project with
                {
                    Root = workspace.Checkout.ProjectRoot?.Replace('\\', '/') ?? project.Root,
                    GitCommit = workspace.Checkout.GitCommit,
                    Dirty = workspace.Checkout.Dirty,
                    SourceTreeHash = workspace.Checkout.SourceTreeHash,
                },
            };
            runs.Update(handle, info);

            var picked = await CutPicker.PickAsync(host.Analyzer, workspace, target.CutQuery, catalog, confirm, ct).ConfigureAwait(false);
            if (picked.Warning is not null)
            {
                warnings.Add(picked.Warning);
            }

            var cutFqn = picked.Cut?.Fqn ?? target.CutQuery;
            info = info with { Targets = [new RunTarget(cutFqn, TargetState.Running)] };
            runs.Update(handle, info);

            var pipeline = new Pipeline(this, host, run, settings, model, limits, templates, catalog, options, repair, confirm, workspace, warnings);
            var result = picked.Cut is null
                ? Blocked(cutFqn, picked.Blocked!, 0)
                : await pipeline.RunAsync(picked.Cut, ct).ConfigureAwait(false);
            runs.WriteJson(handle, $"{ProjectAnalyzer.TargetDir(cutFqn)}/result.json", result);

            var stopped = result.Result == TargetOutcome.Cancelled;
            var totals = new RunTotals(result.LlmCalls, result.Tokens.In, result.Tokens.Out, result.CostUsd);
            info = info with
            {
                Status = stopped ? RunStatus.Failed : RunStatus.Completed,
                FinishedAt = DateTimeOffset.UtcNow,
                Targets = [new RunTarget(cutFqn, stopped ? TargetState.Running : TargetState.Done)],
                Totals = totals,
            };
            runs.Update(handle, info);
            runs.AppendHistory(new HistoryEntry(id, info.StartedAt, project.PathName, [cutFqn],
                result.Result == TargetOutcome.Valid ? 1 : 0, result.Apply?.Decision == ApplyDecision.Written ? 1 : 0, info.Status));
            Report(run, ArrowEventTypes.RunFinished, null, $"Run {id}: {result.Result.ToString().ToUpperInvariant()}");
            return new GenerateResult(id, handle.Root, info.Status, ExitCodeOf(result), [result], totals, warnings);
        }
        catch (OperationCanceledException)
        {
            runs.Update(handle, info with { Status = RunStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow });
            throw;
        }
        catch (Exception)
        {
            runs.Update(handle, info with { Status = RunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow });
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

    /// <summary>Exit code of one target (P07 §9).</summary>
    public static int ExitCodeOf(TargetResult result) => result.Result switch
    {
        TargetOutcome.Valid => ExitValid,
        TargetOutcome.Blocked => ExitBlocked,
        TargetOutcome.Cancelled => result.Error is not null ? ExitBlocked : ExitCancelled,
        _ => ExitNotValid,
    };

    private static TargetResult Blocked(string cutFqn, BlockOutcome outcome, double block1Seconds) => new(
        cutFqn,
        outcome.Outcome == BlockOutcomeKind.Infra ? TargetOutcome.Infra : TargetOutcome.Blocked,
        null, 0, null, null, null, null, null, null, null, null, TokenUsage.Zero, null, 0,
        new BlockSeconds(block1Seconds, null, null, null, block1Seconds),
        new ErrorInfo(outcome.Code ?? ErrorCodes.CliInternal, outcome.Message ?? "", outcome.Hint, outcome.Log is null ? null : $"block1/{outcome.Log}".Replace("block1/block1/", "block1/", StringComparison.Ordinal)),
        [], null);

    private static void Report(RunContext run, string type, string? cut, string message, EventLevel level = EventLevel.Info, params (string Key, object? Value)[] data) =>
        run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, cut, null, null, level, message, data.ToDictionary(d => d.Key, d => d.Value)));

    /// <summary>The target pipeline of P07 §6 for one CUT.</summary>
    private sealed class Pipeline(
        GenerateTestsUseCase owner,
        IAnalyzerHost host,
        RunContext run,
        ArrowConfig settings,
        ResolvedModel model,
        LlmLimits limits,
        IPromptTemplateStore templates,
        ErrorCatalog catalog,
        GenerateOptions options,
        RepairSettings repair,
        IConfirmation confirm,
        ProjectWorkspace workspace,
        List<string> warnings)
    {
        public async Task<TargetResult> RunAsync(ClassCandidate cut, CancellationToken ct)
        {
            var total = Stopwatch.StartNew();
            var analysis = await host.Analyzer.AnalyzeAsync(workspace, cut, run, ct).ConfigureAwait(false);
            var block1 = analysis.Outcome.Seconds;
            warnings.AddRange(analysis.Warnings);
            if (analysis.Outcome.Outcome != BlockOutcomeKind.Ok)
            {
                return Blocked(cut.Fqn, analysis.Outcome, block1);
            }

            var m = analysis.Metadata!;
            var dir = ProjectAnalyzer.TargetDir(m.Cut.Fqn);
            var shared = analysis.SharedFrom is null ? dir : ProjectAnalyzer.TargetDir(analysis.SharedFrom);
            var task = new TaskContext(
                run, m.Cut.Fqn, m, analysis.Baseline!, analysis.Workspace!,
                File.ReadAllText(Path.Combine(workspace.Checkout.Root, m.Paths.CutPath.Replace('/', Path.DirectorySeparatorChar))),
                dir,
                (owner.Runs.ReadText(run.Run, $"{shared}/block1/test_classpath.txt") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
            var repairOptions = new RepairOptions(
                repair.Enabled, repair.Rounds, repair.EarlyStopP, model,
                (owner.Runs.ReadText(run.Run, $"{dir}/block1/metadata_prompt.txt") ?? "").TrimEnd('\n'),
                TimeSpan.FromSeconds(settings.Verification.StepTimeoutSeconds), settings.Verification.InfraAttempts,
                TimeSpan.FromSeconds(settings.Verification.InfraWaitSeconds), settings.Run.KeepWorkspaces);

            var llm = owner.Llms.Create(model, run.Run, limits, run.Progress);
            var generator = new TestGenerator(llm, host.JavaHelpers, templates, owner.Runs);

            // Block 2: c0.
            var watch = Stopwatch.StartNew();
            GenerationResult c0;
            try
            {
                c0 = await generator.GenerateAsync(new GenerationContext(run.Run, m.Cut.Fqn, m, repairOptions.MetadataPrompt, task.CutSource, model, $"{dir}/block2", run.Progress), ct).ConfigureAwait(false);
            }
            catch (ArrowException ex)
            {
                var stop = RepairRunner.StopCodes.Contains(ex.Code);
                return Result(m, stop ? TargetOutcome.Cancelled : TargetOutcome.Infra, null, null, catalog.Describe(ex), block1, watch.Elapsed.TotalSeconds, null, total);
            }

            var block2 = watch.Elapsed.TotalSeconds;
            var calls = new Usage(c0.Response);
            if (!c0.Candidate.ValidOutput)
            {
                var invalid = catalog.Describe(ErrorCodes.B2InvalidOutput, c0.Candidate.InvalidReason);
                return Result(m, TargetOutcome.InvalidOutput, null, null, invalid, block1, block2, null, total, calls);
            }

            // Block 3: verify c0, then the chain.
            watch.Restart();
            var v0 = await host.Verifier.VerifyAsync(
                SuiteFiles.Single(c0.Candidate.TestFqn, c0.Candidate.TestPath, c0.Candidate.Code!), task,
                repairOptions.Verify($"{dir}/block3/c0", 0, c0.Candidate), ct).ConfigureAwait(false);
            owner.Runs.WriteJson(run.Run, $"{dir}/block3/verify_c0.json", v0);
            if (v0.IsInfra)
            {
                var infra = catalog.Describe(v0.InfraReason!, v0.BuildErrorExcerpt, log: "block3/c0/logs/");
                return Result(m, TargetOutcome.Infra, null, null, infra, block1, block2, watch.Elapsed.TotalSeconds, total, calls);
            }

            var runner = new RepairRunner(generator, host.Verifier, new FailureDiagnoser(), new ExtraContextBuilder(host.JavaHelpers), host.JavaHelpers, owner.Runs, catalog);
            var final = await runner.RunChainAsync(c0.Candidate, v0, task, repairOptions, ct).ConfigureAwait(false);
            var block3 = watch.Elapsed.TotalSeconds;
            calls = calls.Add(final);
            if (final.DroppedMethods.Count > 0)
            {
                warnings.Add($"{final.DroppedMethods.Count} test method đã bị bỏ trong lúc sửa: {string.Join(", ", final.DroppedMethods)}");
            }

            var outcome = final.StopReason switch
            {
                StopReason.Stopped or StopReason.Cancelled => TargetOutcome.Cancelled,
                StopReason.Infra => TargetOutcome.Infra,
                _ => final.FinalState.ToOutcome(),
            };
            if (final.StopReason == StopReason.Cancelled)
            {
                ct.ThrowIfCancellationRequested();
            }

            ApplySummary? apply = null;
            if (outcome != TargetOutcome.Cancelled || final.Error is not null)
            {
                apply = await ApplyAsync(m, final, ct).ConfigureAwait(false);
            }

            return Result(m, outcome, v0.State, final, final.Error, block1, block2, block3, total, calls) with { Apply = apply };
        }

        private TargetResult Result(
            RepositoryMetadata m, TargetOutcome outcome, VerificationState? c0State, FinalTestSuite? final, ErrorInfo? error,
            double block1, double? block2, double? block3, Stopwatch total, Usage? usage = null)
        {
            var metricsSkipped = options.NoMetrics ? "disabled" : "not_available";
            if (!options.NoMetrics && outcome == TargetOutcome.Valid)
            {
                warnings.Add("Bản này chưa có khối 4 (JaCoCo, PIT, tsDetect): chưa đo chất lượng. Dùng --no-metrics để ẩn cảnh báo này.");
            }

            return new TargetResult(
                m.Cut.Fqn, outcome, c0State, final?.RoundsRun ?? 0, final?.SuccessRound, final?.StopReason,
                final?.Candidate.TestFqn ?? m.Paths.GeneratedTestFqn, final?.Candidate.TestPath ?? m.Paths.GeneratedTestPath,
                final is null ? null : "block3/final.java",
                null, metricsSkipped, null,
                usage?.Tokens ?? TokenUsage.Zero, usage?.Cost, usage?.Calls ?? 0,
                new BlockSeconds(Round(block1), Round(block2), Round(block3), null, Math.Round(total.Elapsed.TotalSeconds, 1)),
                error,
                final?.DroppedMethods.Count > 0 ? [$"dropped_methods: {final.DroppedMethods.Count}"] : [],
                null);
        }

        private static double? Round(double? seconds) => seconds is { } s ? Math.Round(s, 1) : null;

        /// <summary>The simple writer of M1 (P06 §3, §4.1, §5): VALID only, a local project, the CUT unchanged, never overwrite.</summary>
        private async Task<ApplySummary> ApplyAsync(RepositoryMetadata m, FinalTestSuite final, CancellationToken ct)
        {
            var path = final.Candidate.TestPath;
            var decision = ApplyDecision.Blocked;
            ErrorInfo? error = null;
            string? written = null;
            var eol = LineEndings.Lf;
            var tags = new List<string>();
            if (!m.Paths.NameMatchesTestIncludes || final.GeneratedTestRanInModule == false)
            {
                tags.Add("surefire_includes_mismatch");
                warnings.Add($"mvn test của project sẽ không tự chạy file này. Hãy thêm mẫu tên vào cấu hình Surefire, hoặc chạy riêng bằng -Dtest={m.Paths.GeneratedTestFqn[(m.Paths.GeneratedTestFqn.LastIndexOf('.') + 1)..]}");
            }

            var root = workspace.Checkout.ProjectRoot;
            if (!final.Success)
            {
                decision = ApplyDecision.NotValid;
            }
            else if (root is null)
            {
                decision = ApplyDecision.RemoteProject;
            }
            else if (options.DryRun)
            {
                decision = ApplyDecision.DryRun;
            }
            else
            {
                var cutFile = Path.Combine(root, m.Paths.CutPath.Replace('/', Path.DirectorySeparatorChar));
                var current = File.Exists(cutFile) ? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(cutFile, ct).ConfigureAwait(false))) : null;
                eol = current is null ? LineEndings.Lf : ProjectFileWriter.EndingsOf(await File.ReadAllTextAsync(cutFile, ct).ConfigureAwait(false));
                var changed = current != m.Cut.SourceSha256;
                var question = $"Ghi {path} ({final.Candidate.TestMethodCount} test)?";
                bool write;
                if (changed)
                {
                    error = catalog.Describe(ErrorCodes.WProjectChanged);
                    write = !options.Yes && confirm.CanAsk
                            && await confirm.ConfirmAsync("CUT đã thay đổi sau khi ARROW đọc nó; test được xác minh trên bản cũ. Vẫn ghi?", false, ct).ConfigureAwait(false);
                    decision = write ? decision : ApplyDecision.Blocked;
                }
                else if (options.Yes || !settings.Apply.Confirm)
                {
                    write = true;
                }
                else if (!confirm.CanAsk)
                {
                    write = false;
                    decision = ApplyDecision.SkippedNonInteractive;
                    warnings.Add("Bỏ qua: cần --yes khi chạy không tương tác");
                }
                else
                {
                    write = await confirm.ConfirmAsync(question, true, ct).ConfigureAwait(false);
                    decision = write ? decision : ApplyDecision.Declined;
                }

                if (write)
                {
                    try
                    {
                        written = await owner.Writer.WriteNewAsync(root, path, final.Candidate.Code!, eol, ct).ConfigureAwait(false);
                        decision = ApplyDecision.Written;
                        error = null;
                        Report(run, ArrowEventTypes.ApplyWritten, m.Cut.Fqn, $"Đã ghi {path}", EventLevel.Info, ("path", path));
                    }
                    catch (ArrowException ex)
                    {
                        decision = ApplyDecision.Blocked;
                        error = catalog.Describe(ex);
                    }
                }
            }

            if (decision != ApplyDecision.Written)
            {
                Report(run, ArrowEventTypes.ApplySkipped, m.Cut.Fqn, $"Không ghi file ({decision.ToString().ToLowerInvariant()})", EventLevel.Info, ("decision", decision));
            }

            owner.Runs.WriteJson(run.Run, $"{ProjectAnalyzer.TargetDir(m.Cut.Fqn)}/apply.json", new ApplyRecord(
                decision, decision is ApplyDecision.Written or ApplyDecision.DryRun or ApplyDecision.Declined or ApplyDecision.SkippedNonInteractive ? path : null,
                final.Candidate.TestFqn, m.Paths.NameIndex, false, false,
                eol == LineEndings.Crlf ? "crlf" : "lf", tags, error, DateTimeOffset.UtcNow));
            return new ApplySummary(decision, written is null ? (decision == ApplyDecision.DryRun ? path : null) : path);
        }
    }

    /// <summary>Model calls, tokens and cost of a target.</summary>
    private sealed record Usage(int Calls, TokenUsage Tokens, double? Cost)
    {
        public Usage(Common.Llm.LlmResponse response)
            : this(response.Attempts, new TokenUsage(response.PromptTokens, response.CompletionTokens), response.CostUsd)
        {
        }

        public Usage Add(FinalTestSuite final) => new(
            Calls + final.LlmCalls,
            new TokenUsage(Tokens.In + final.Tokens.In, Tokens.Out + final.Tokens.Out),
            Cost is null && final.CostUsd is null ? null : (Cost ?? 0) + (final.CostUsd ?? 0));
    }
}

/// <summary><c>apply.json</c> (P06 §6).</summary>
public sealed record ApplyRecord(
    ApplyDecision Decision,
    string? Path,
    string TestFqn,
    int NameIndex,
    bool RenamedAfterRun,
    bool Reverified,
    string LineEndings,
    IReadOnlyList<string> Warnings,
    ErrorInfo? Error,
    DateTimeOffset At);
