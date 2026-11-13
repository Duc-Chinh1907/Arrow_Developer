using System.Diagnostics;
using Arrow.Business.Analysis;
using Arrow.Business.Generation;
using Arrow.Business.Llm;
using Arrow.Business.Quality;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Generation;
using Arrow.Common.Llm;
using Arrow.Common.Quality;
using Arrow.Common.Verification;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.UseCases;

/// <summary>A finished target before the write step: its result and what the write step needs.</summary>
/// <param name="Result">The content of <c>result.json</c>, without <c>apply</c> yet.</param>
/// <param name="Final">The final suite; null when block 3 did not finish.</param>
/// <param name="Metadata">M; null when block 1 did not give it.</param>
/// <param name="Warnings">Warnings of this target.</param>
/// <param name="Task">Block 3 context, for a verification after renaming (P04 §8); null when block 1 did not finish.</param>
public sealed record TargetRun(TargetResult Result, FinalTestSuite? Final, RepositoryMetadata? Metadata, IReadOnlyList<string> Warnings, TaskContext? Task = null);

/// <summary>What every target of a run shares (P07 §7).</summary>
/// <param name="Host">Block 1, java-helper, the verifier and the build tools.</param>
/// <param name="Llm">One model client for the run, so that its limits and budget are shared.</param>
/// <param name="Model">The model.</param>
/// <param name="Templates">Prompt templates.</param>
/// <param name="Catalog">Error texts.</param>
/// <param name="Settings">The configuration.</param>
/// <param name="Repair">Repair settings after the options.</param>
/// <param name="Measure">Block 4 settings; null when measuring is off.</param>
/// <param name="Workspace">The code of the run.</param>
/// <param name="Runs">Results of runs.</param>
/// <param name="Tools">Tools of block 4.</param>
/// <param name="Processes">Starts <c>java</c> for block 4.</param>
public sealed record PipelineShared(
    IAnalyzerHost Host,
    ILlmClient Llm,
    ResolvedModel Model,
    IPromptTemplateStore Templates,
    ErrorCatalog Catalog,
    ArrowConfig Settings,
    RepairSettings Repair,
    QualityOptions? Measure,
    ProjectWorkspace Workspace,
    IRunStore Runs,
    IToolStore Tools,
    IProcessRunner Processes);

/// <summary>The pipeline of one CUT (P07 §6): block 1, c0, its verification, the repair chain and block 4. Writing comes later.</summary>
public static class TargetPipeline
{
    /// <summary>Runs the pipeline; a failure of a block is a result, only a cancellation raises.</summary>
    public static async Task<TargetRun> RunAsync(ClassCandidate cut, RunContext run, PipelineShared shared, CancellationToken ct)
    {
        var warnings = new List<string>();
        var total = Stopwatch.StartNew();
        var analysis = await shared.Host.Analyzer.AnalyzeAsync(shared.Workspace, cut, run, ct).ConfigureAwait(false);
        var block1 = analysis.Outcome.Seconds;
        warnings.AddRange(analysis.Warnings);
        if (analysis.Outcome.Outcome != BlockOutcomeKind.Ok)
        {
            return new TargetRun(Blocked(cut.Fqn, analysis.Outcome, block1), null, analysis.Metadata, warnings);
        }

        var m = analysis.Metadata!;
        var runs = shared.Runs;
        var catalog = shared.Catalog;
        var settings = shared.Settings;
        var dir = ProjectAnalyzer.TargetDir(m.Cut.Fqn);
        var sharedDir = analysis.SharedFrom is null ? dir : ProjectAnalyzer.TargetDir(analysis.SharedFrom);
        var task = new TaskContext(
            run, m.Cut.Fqn, m, analysis.Baseline!, analysis.Workspace!,
            File.ReadAllText(Path.Combine(shared.Workspace.Checkout.Root, m.Paths.CutPath.Replace('/', Path.DirectorySeparatorChar))),
            dir,
            (runs.ReadText(run.Run, $"{sharedDir}/block1/test_classpath.txt") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var repair = shared.Repair;
        var repairOptions = new RepairOptions(
            repair.Enabled, repair.Rounds, repair.EarlyStopP, shared.Model,
            (runs.ReadText(run.Run, $"{dir}/block1/metadata_prompt.txt") ?? "").TrimEnd('\n'),
            TimeSpan.FromSeconds(settings.Verification.StepTimeoutSeconds), settings.Verification.InfraAttempts,
            TimeSpan.FromSeconds(settings.Verification.InfraWaitSeconds), settings.Run.KeepWorkspaces);
        var generator = new TestGenerator(shared.Llm, shared.Host.JavaHelpers, shared.Templates, runs);

        TargetRun Done(TargetOutcome outcome, VerificationState? c0State, FinalTestSuite? final, ErrorInfo? error, double? block2, double? block3, Usage? usage = null, QualityResult? quality = null, double? block4 = null) =>
            new(Result(m, outcome, c0State, final, error, block1, block2, block3, total, shared.Measure is null, usage, quality, block4), final, m, warnings, task);

        // Block 2: c0.
        var watch = Stopwatch.StartNew();
        GenerationResult c0;
        try
        {
            c0 = await generator.GenerateAsync(new GenerationContext(run.Run, m.Cut.Fqn, m, repairOptions.MetadataPrompt, task.CutSource, shared.Model, $"{dir}/block2", run.Progress), ct).ConfigureAwait(false);
        }
        catch (ArrowException ex)
        {
            var stop = RepairRunner.StopCodes.Contains(ex.Code);
            return Done(stop ? TargetOutcome.Cancelled : TargetOutcome.Infra, null, null, catalog.Describe(ex), watch.Elapsed.TotalSeconds, null);
        }

        var block2 = watch.Elapsed.TotalSeconds;
        var usage = new Usage(c0.Response);
        if (!c0.Candidate.ValidOutput)
        {
            return Done(TargetOutcome.InvalidOutput, null, null, catalog.Describe(ErrorCodes.B2InvalidOutput, c0.Candidate.InvalidReason), block2, null, usage);
        }

        // Block 3: verify c0, then the chain.
        watch.Restart();
        var v0 = await shared.Host.Verifier.VerifyAsync(
            SuiteFiles.Single(c0.Candidate.TestFqn, c0.Candidate.TestPath, c0.Candidate.Code!), task,
            repairOptions.Verify($"{dir}/block3/c0", 0, c0.Candidate), ct).ConfigureAwait(false);
        runs.WriteJson(run.Run, $"{dir}/block3/verify_c0.json", v0);
        if (v0.IsInfra)
        {
            return Done(TargetOutcome.Infra, null, null, catalog.Describe(v0.InfraReason!, v0.BuildErrorExcerpt, log: "block3/c0/logs/"), block2, watch.Elapsed.TotalSeconds, usage);
        }

        var runner = new RepairRunner(generator, shared.Host.Verifier, new FailureDiagnoser(), new ExtraContextBuilder(shared.Host.JavaHelpers), shared.Host.JavaHelpers, runs, catalog);
        var final = await runner.RunChainAsync(c0.Candidate, v0, task, repairOptions, ct).ConfigureAwait(false);
        var block3 = watch.Elapsed.TotalSeconds;
        usage = usage.Add(final);
        if (final.DroppedMethods.Count > 0)
        {
            warnings.Add($"{m.Cut.SimpleName}: {final.DroppedMethods.Count} test method đã bị bỏ trong lúc sửa: {string.Join(", ", final.DroppedMethods)}");
        }

        if (final.StopReason == StopReason.Cancelled)
        {
            ct.ThrowIfCancellationRequested();
        }

        var outcome = final.StopReason switch
        {
            StopReason.Stopped => TargetOutcome.Cancelled,
            StopReason.Infra => TargetOutcome.Infra,
            _ => final.FinalState.ToOutcome(),
        };

        // Block 4: only the final VALID suite, when measuring is on (P05 §2).
        QualityResult? quality = null;
        double? block4 = null;
        if (shared.Measure is not null && final.Success)
        {
            watch.Restart();
            var suite = new SuiteToMeasure(final.Candidate.TestFqn, final.Candidate.TestPath, final.Candidate.Code!);
            quality = await QualityServiceOf(shared).MeasureAsync(suite, task, shared.Measure, ct).ConfigureAwait(false);
            block4 = watch.Elapsed.TotalSeconds;
            foreach (var (tool, state) in quality.Tools.Where(t => t.Value.Status != ToolStatus.Ok))
            {
                warnings.Add($"{m.Cut.SimpleName}: chưa đo được {tool} ({state.Code}); xem {Path.Combine(run.Run.Root, "targets", m.Cut.Fqn, "block4", "logs")}");
            }
        }

        return Done(outcome, v0.State, final, final.Error, block2, block3, usage, quality, block4);
    }

    /// <summary>The result of a target that block 1 could not use, or a query that named no class.</summary>
    public static TargetResult Blocked(string cutFqn, BlockOutcome outcome, double block1Seconds) => new(
        cutFqn,
        outcome.Outcome == BlockOutcomeKind.Infra ? TargetOutcome.Infra : TargetOutcome.Blocked,
        null, 0, null, null, null, null, null, null, null, null, TokenUsage.Zero, null, 0,
        new BlockSeconds(Math.Round(block1Seconds, 1), null, null, null, Math.Round(block1Seconds, 1)),
        new ErrorInfo(outcome.Code ?? ErrorCodes.CliInternal, outcome.Message ?? "", outcome.Hint, outcome.Log is null ? null : $"block1/{outcome.Log}".Replace("block1/block1/", "block1/", StringComparison.Ordinal)),
        [], null);

    private static TargetResult Result(
        RepositoryMetadata m, TargetOutcome outcome, VerificationState? c0State, FinalTestSuite? final, ErrorInfo? error,
        double block1, double? block2, double? block3, Stopwatch total, bool metricsOff, Usage? usage, QualityResult? quality, double? block4) => new(
        m.Cut.Fqn, outcome, c0State, final?.RoundsRun ?? 0, final?.SuccessRound, final?.StopReason,
        final?.Candidate.TestFqn ?? m.Paths.GeneratedTestFqn, final?.Candidate.TestPath ?? m.Paths.GeneratedTestPath,
        final is null ? null : "block3/final.java",
        quality?.Metrics, metricsOff ? "disabled" : null, quality?.Tools.ToDictionary(t => t.Key, t => t.Value.Status),
        usage?.Tokens ?? TokenUsage.Zero, usage?.Cost, usage?.Calls ?? 0,
        new BlockSeconds(Round(block1), Round(block2), Round(block3), Round(block4), Math.Round(total.Elapsed.TotalSeconds, 1)),
        error,
        final?.DroppedMethods.Count > 0 ? [$"dropped_methods: {final.DroppedMethods.Count}"] : [],
        null);

    private static double? Round(double? seconds) => seconds is { } s ? Math.Round(s, 1) : null;

    private static QualityService QualityServiceOf(PipelineShared shared)
    {
        var java = new JavaRunner(shared.Processes);
        var host = shared.Host;
        var preparer = new SuitePreparer(host.Builds, host.Workspaces);
        return new QualityService(
        [
            new JacocoTool(preparer, host.Builds, shared.Tools, java, host.Workspaces, shared.Runs),
            new PitTool(preparer, shared.Tools, java, host.Workspaces, shared.Runs),
            new TsDetectTool(shared.Tools, host.JavaHelpers, java, host.Workspaces, shared.Runs),
        ], shared.Runs, shared.Catalog);
    }

    /// <summary>Model calls, tokens and cost of a target.</summary>
    private sealed record Usage(int Calls, TokenUsage Tokens, double? Cost)
    {
        public Usage(LlmResponse response)
            : this(response.Attempts, new TokenUsage(response.PromptTokens, response.CompletionTokens), response.CostUsd)
        {
        }

        public Usage Add(FinalTestSuite final) => new(
            Calls + final.LlmCalls,
            new TokenUsage(Tokens.In + final.Tokens.In, Tokens.Out + final.Tokens.Out),
            Cost is null && final.CostUsd is null ? null : (Cost ?? 0) + (final.CostUsd ?? 0));
    }
}
