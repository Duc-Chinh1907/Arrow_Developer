using System.Diagnostics;
using System.Text.Json.Serialization;
using Arrow.Business.Generation;
using Arrow.Business.Llm;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Generation;
using Arrow.Common.Verification;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Repair;

/// <summary>Settings of a repair chain (P04 §2).</summary>
/// <param name="Enabled"><c>repair.enabled</c>; false with <c>--no-repair</c>.</param>
/// <param name="Rounds">N: <c>repair.rounds</c> or <c>--rounds</c>.</param>
/// <param name="EarlyStopP"><c>repair.early_stop_p</c>; null turns the early stop off.</param>
/// <param name="Model">The model of the run.</param>
/// <param name="MetadataPrompt">The text of M for the prompts.</param>
/// <param name="StepTimeout"><c>verification.step_timeout_seconds</c>.</param>
/// <param name="InfraAttempts"><c>verification.infra_attempts</c>.</param>
/// <param name="InfraWait"><c>verification.infra_wait_seconds</c>.</param>
/// <param name="KeepWorkspaces"><c>run.keep_workspaces</c>.</param>
public sealed record RepairOptions(
    bool Enabled,
    int Rounds,
    int? EarlyStopP,
    ResolvedModel Model,
    string MetadataPrompt,
    TimeSpan StepTimeout,
    int InfraAttempts,
    TimeSpan InfraWait,
    bool KeepWorkspaces)
{
    /// <summary>The settings of the verification of round <paramref name="round"/> (0 for c0).</summary>
    public VerifyOptions Verify(string outputDir, int round, CandidateTest candidate) =>
        new(outputDir, round, StepTimeout, InfraAttempts, InfraWait, KeepWorkspaces, candidate.CodeHash, candidate.AssertionCount);
}

/// <summary><c>final.json</c> of a CUT (P04 §7), with the final suite for the caller.</summary>
public sealed record FinalTestSuite
{
    /// <summary>The CUT.</summary>
    public required string CutFqn { get; init; }

    /// <summary>State of c0.</summary>
    public required VerificationState C0State { get; init; }

    /// <summary>The final suite is VALID.</summary>
    public bool Success => FinalState == VerificationState.Valid;

    /// <summary>Round that reached VALID (0 for c0); null when the suite never did (differs from R03, P04 §7).</summary>
    public int? SuccessRound { get; init; }

    /// <summary>Repair rounds run.</summary>
    public int RoundsRun { get; init; }

    /// <summary>Why the chain stopped; null when no chain ran (c0 VALID or <c>--no-repair</c>).</summary>
    public StopReason? StopReason { get; init; }

    /// <summary>State of the final suite.</summary>
    public required VerificationState FinalState { get; init; }

    /// <summary>Round of the final suite.</summary>
    public int FinalRound { get; init; }

    /// <summary>File of the final suite, next to <c>final.json</c>.</summary>
    public string FinalCode { get; init; } = "final.java";

    /// <summary>Model calls and builds of the rounds, in seconds; replayed calls count with the time of the real call.</summary>
    public double RepairSeconds { get; init; }

    /// <summary>Model time of the rounds.</summary>
    public double LlmSeconds { get; init; }

    /// <summary>Build time of the rounds.</summary>
    public double BuildSeconds { get; init; }

    /// <summary>Tokens of the rounds.</summary>
    public TokenUsage Tokens { get; init; } = TokenUsage.Zero;

    /// <summary>Cost of the rounds; null when the price of the model is unknown.</summary>
    public double? CostUsd { get; init; }

    /// <summary>Counters of the chain.</summary>
    public RepairCounters Counters { get; init; } = new();

    /// <summary>Test methods of c0 that are not in the final suite (level 3).</summary>
    public IReadOnlyList<string> DroppedMethods { get; init; } = [];

    /// <summary>The final suite ran with the module in step 5; null when step 5 did not run.</summary>
    public bool? GeneratedTestRanInModule { get; init; }

    /// <summary>The final candidate.</summary>
    [JsonIgnore]
    public CandidateTest Candidate { get; init; } = null!;

    /// <summary>The verification of the final candidate.</summary>
    [JsonIgnore]
    public VerifyResult Verify { get; init; } = null!;

    /// <summary>Model calls of the rounds.</summary>
    [JsonIgnore]
    public int LlmCalls { get; init; }

    /// <summary>The error behind <c>infra</c> and <c>stopped</c>; null otherwise.</summary>
    [JsonIgnore]
    public ErrorInfo? Error { get; init; }
}

/// <summary>Runs the repair chain of a CUT (P04 §6–7).</summary>
public interface IRepairRunner
{
    /// <summary>
    /// Repairs <paramref name="c0"/> until VALID, the budget, stagnation, an infrastructure failure, a stop of the run or a
    /// cancellation, and writes <c>block3/round_&lt;k&gt;/</c>, <c>final.json</c> and <c>final.java</c>. c0 must have code and
    /// a verification that is not INFRA. A c0 that is VALID, or repair turned off, gives c0 as the final suite.
    /// </summary>
    Task<FinalTestSuite> RunChainAsync(CandidateTest c0, VerifyResult c0Result, TaskContext task, RepairOptions options, CancellationToken ct);
}

/// <summary>
/// <see cref="IRepairRunner"/>: the loop of P04 §6.3 around <see cref="AdaptiveRepairPolicy"/>. Each round writes
/// <c>request.json</c> before the model call, block 2 writes its files, and the round ends with <c>verify.json</c> (when it was
/// verified) and <c>decision.json</c>. A cancellation ends the chain with <c>cancelled</c> instead of an exception, so that
/// the checkpoint is kept.
/// </summary>
public sealed class RepairRunner(
    ITestGenerator generator,
    IVerifier verifier,
    IFailureDiagnoser diagnoser,
    ExtraContextBuilder extraContext,
    IJavaHelperProvider javaHelpers,
    IRunStore runs,
    ErrorCatalog catalog) : IRepairRunner
{
    /// <summary>Codes of P03 that stop the run (P04 §6.2, <c>stopped</c>).</summary>
    public static readonly IReadOnlySet<string> StopCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ErrorCodes.B2LlmConfig, ErrorCodes.B2WrongModel, ErrorCodes.B2TokenBudget,
    };

    /// <inheritdoc />
    public async Task<FinalTestSuite> RunChainAsync(CandidateTest c0, VerifyResult c0Result, TaskContext task, RepairOptions options, CancellationToken ct)
    {
        var c0State = c0Result.State ?? throw new ArgumentException("An INFRA c0 does not enter the chain.", nameof(c0Result));
        if (c0State == VerificationState.Valid || !options.Enabled)
        {
            var direct = new FinalTestSuite
            {
                CutFqn = task.CutFqn,
                C0State = c0State,
                SuccessRound = c0State == VerificationState.Valid ? 0 : null,
                FinalState = c0State,
                GeneratedTestRanInModule = c0Result.GeneratedTestRanInModule,
                Candidate = c0,
                Verify = c0Result,
            };
            Write(task, direct);
            Report(task, ArrowEventTypes.B3Final, $"Kết thúc: {Name(c0State)}{(options.Enabled ? "" : " (không sửa)")}",
                ("stop_reason", null), ("final_state", Name(c0State)), ("rounds", 0));
            return direct;
        }

        var policy = new AdaptiveRepairPolicy(new ChainVersion(0, c0, c0Result, diagnoser.Diagnose(c0Result, c0, task.Metadata)), task.CutFqn, options.EarlyStopP);
        var totals = new Totals();
        StopReason? stop = null;
        ErrorInfo? error = null;
        var round = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            for (round = 1; round <= options.Rounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                var (outcome, decision) = await RoundAsync(policy, round, task, options, totals, ct).ConfigureAwait(false);
                if (outcome is RoundEnd.Infra infra)
                {
                    (stop, error) = (Common.StopReason.Infra, infra.Error);
                    break;
                }

                if (decision?.State == VerificationState.Valid)
                {
                    stop = Common.StopReason.Valid;
                    break;
                }

                if (!policy.CanContinue(round, options.Rounds))
                {
                    stop = policy.StopReason;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stop = Common.StopReason.Cancelled;
            round--;
        }
        catch (ArrowException ex) when (StopCodes.Contains(ex.Code))
        {
            (stop, error) = (Common.StopReason.Stopped, catalog.Describe(ex));
            round--;
        }
        catch (ArrowException ex)
        {
            // Model unavailable after its retries, or java-helper down: an infrastructure failure of the chain (P04 §6.2).
            (stop, error) = (Common.StopReason.Infra, catalog.Describe(ex));
            round--;
        }

        var best = policy.Best;
        var suite = new FinalTestSuite
        {
            CutFqn = task.CutFqn,
            C0State = c0State,
            SuccessRound = best.State == VerificationState.Valid ? best.Round : null,
            RoundsRun = Math.Min(round, options.Rounds),
            StopReason = stop ?? Common.StopReason.Budget,
            FinalState = best.State,
            FinalRound = best.Round,
            RepairSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            LlmSeconds = Math.Round(totals.LlmSeconds, 1),
            BuildSeconds = Math.Round(totals.BuildSeconds, 1),
            Tokens = new TokenUsage(totals.In, totals.Out),
            CostUsd = totals.Cost,
            Counters = policy.Counters,
            DroppedMethods = await DroppedMethodsAsync(c0, best.Candidate).ConfigureAwait(false),
            GeneratedTestRanInModule = best.Verify.GeneratedTestRanInModule,
            Candidate = best.Candidate,
            Verify = best.Verify,
            LlmCalls = totals.Calls,
            Error = error,
        };
        Write(task, suite);
        Report(task, ArrowEventTypes.B3Final,
            $"Kết thúc ({StopText(suite.StopReason)}): {Name(suite.FinalState)} sau {suite.RoundsRun} vòng",
            ("stop_reason", StopText(suite.StopReason)), ("final_state", Name(suite.FinalState)), ("rounds", suite.RoundsRun));
        return suite;
    }

    private abstract record RoundEnd
    {
        public sealed record Done : RoundEnd;

        public sealed record Infra(ErrorInfo Error) : RoundEnd;
    }

    private sealed class Totals
    {
        public double LlmSeconds { get; set; }

        public double BuildSeconds { get; set; }

        public long In { get; set; }

        public long Out { get; set; }

        public double? Cost { get; set; }

        public int Calls { get; set; }
    }

    /// <summary>One round: request, model, verification (unless the code is new and usable), decision.</summary>
    private async Task<(RoundEnd Outcome, RoundDecision? Decision)> RoundAsync(AdaptiveRepairPolicy policy, int round, TaskContext task, RepairOptions options, Totals totals, CancellationToken ct)
    {
        var dir = RoundDir(task, round);
        var request = policy.NextRequest();
        IReadOnlyList<string> warnings = [];
        if (request.Level >= 2)
        {
            var extra = await extraContext.BuildAsync(policy.Best, task, ct).ConfigureAwait(false);
            request = request with { ExtraContext = extra.Text.Length == 0 ? null : extra.Text };
            warnings = extra.Warnings;
        }

        runs.WriteJson(task.Run.Run, dir + "/request.json", request);
        var context = new GenerationContext(task.Run.Run, task.CutFqn, task.Metadata, options.MetadataPrompt, task.CutSource, options.Model, dir, task.Run.Progress);
        var generated = await generator.RepairAsync(context, request, ct).ConfigureAwait(false);
        var response = generated.Response;
        totals.Calls += response.Attempts;
        totals.LlmSeconds += response.LatencySeconds;
        totals.In += response.PromptTokens;
        totals.Out += response.CompletionTokens;
        totals.Cost = response.CostUsd is { } cost ? (totals.Cost ?? 0) + cost : totals.Cost;

        var candidate = generated.Candidate;
        VerifyResult? verify = null;
        Diagnosis? diagnosis = null;
        if (candidate.ValidOutput && policy.DuplicateOf(candidate.CodeHash) is null)
        {
            verify = await verifier.VerifyAsync(Suite(candidate), task, options.Verify(dir, round, candidate), ct).ConfigureAwait(false);
            runs.WriteJson(task.Run.Run, dir + "/verify.json", verify);
            totals.BuildSeconds += verify.BuildSeconds;
            if (verify.IsInfra)
            {
                var infra = catalog.Describe(verify.InfraReason!, verify.BuildErrorExcerpt, log: $"block3/round_{round}/logs/");
                Report(task, ArrowEventTypes.B3Round, $"vòng {round} · INFRA {verify.InfraReason}", ("round", round), ("infra_reason", verify.InfraReason));
                return (new RoundEnd.Infra(infra), null);
            }

            diagnosis = verify.State == VerificationState.Valid ? null : diagnoser.Diagnose(verify, candidate, task.Metadata);
        }

        var decision = policy.Apply(new RoundOutcome(request, candidate, verify, diagnosis)) with
        {
            ExtraContextWarnings = warnings,
            LlmSeconds = Math.Round(response.LatencySeconds, 1),
            BuildSeconds = verify?.BuildSeconds ?? 0,
            LlmTokens = new TokenUsage(response.PromptTokens, response.CompletionTokens),
            LlmRetries = Math.Max(0, response.Attempts - 1),
            Replayed = response.Replayed,
        };
        runs.WriteJson(task.Run.Run, dir + "/decision.json", decision);
        Report(task, ArrowEventTypes.B3Round, RoundText(decision),
            ("round", round), ("requested_level", decision.RequestedLevel), ("level", decision.Level),
            ("state", decision.State is { } s ? Name(s) : null), ("signature", decision.ErrorSignature), ("comparison", decision.Comparison?.ToString().ToUpperInvariant()),
            ("decision", DecisionText(decision)), ("tests_passed", decision.TestsPassed), ("seconds", decision.LlmSeconds + decision.BuildSeconds));
        return (new RoundEnd.Done(), decision);
    }

    /// <summary>The line of a round, as the CLI shows it (P08 §3.2): <c>vòng 3 · mức 2 → ASSERTION 4/5 · tốt hơn · checkpoint</c>.</summary>
    public static string RoundText(RoundDecision d)
    {
        var level = d.RequestedLevel == d.Level ? $"mức {d.Level}" : $"mức {d.Level} (yêu cầu {d.RequestedLevel})";
        var result = d.InvalidOutput ? "output không dùng được"
            : d.DuplicateCode ? $"mã trùng vòng {d.DuplicateOfRound}"
            : d.State == VerificationState.Valid ? "VALID ✓"
            : $"{(d.State is { } s ? Name(s) : "?")}{(d.TestsPassed is { } p && d.State is VerificationState.Assertion or VerificationState.Regression ? $" {p} pass" : "")}";
        return $"vòng {d.Round} · {level} → {result} · {DecisionText(d)}";
    }

    /// <summary>What the policy decided: checkpoint, rollback, level change or keep.</summary>
    public static string DecisionText(RoundDecision d) =>
        d.Improved ? (d.State == VerificationState.Valid ? "thành công" : "tốt hơn · checkpoint")
        : d.Rollback ? $"tệ hơn · rollback, lên mức {d.LevelAfter}"
        : d.Level != d.LevelAfter || d.RequestedLevel != d.LevelAfter ? $"lên mức {d.LevelAfter}"
        : "giữ mức";

    private static SuiteFiles Suite(CandidateTest candidate) => SuiteFiles.Single(candidate.TestFqn, candidate.TestPath, candidate.Code ?? "");

    /// <summary><c>targets/&lt;cut&gt;/block3/round_&lt;k&gt;</c>.</summary>
    public static string RoundDir(TaskContext task, int round) => $"{task.TargetDir}/block3/round_{round}";

    private void Write(TaskContext task, FinalTestSuite suite)
    {
        runs.WriteJson(task.Run.Run, $"{task.TargetDir}/block3/final.json", suite);
        runs.WriteText(task.Run.Run, $"{task.TargetDir}/block3/final.java", suite.Candidate.Code ?? "");
    }

    private async Task<IReadOnlyList<string>> DroppedMethodsAsync(CandidateTest c0, CandidateTest final)
    {
        if (ReferenceEquals(c0, final) || c0.Code is null || final.Code is null)
        {
            return [];
        }

        try
        {
            using var lease = await javaHelpers.RentAsync(CancellationToken.None).ConfigureAwait(false);
            var before = await lease.Helper.ListTestMethodsAsync(c0.Code, CancellationToken.None).ConfigureAwait(false);
            var after = await lease.Helper.ListTestMethodsAsync(final.Code, CancellationToken.None).ConfigureAwait(false);
            return before.Except(after, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is ArrowException or JavaHelperCommandException or IOException)
        {
            return [];
        }
    }

    private static string Name(VerificationState state) => state.ToString().ToUpperInvariant();

    private static string StopText(StopReason? reason) => reason?.ToString().ToLowerInvariant() ?? "-";

    private static void Report(TaskContext task, string type, string message, params (string Key, object? Value)[] data) =>
        task.Run.Progress.Report(new ArrowEvent(DateTimeOffset.UtcNow, type, task.CutFqn, null, null, EventLevel.Info, message,
            data.ToDictionary(d => d.Key, d => d.Value)));
}
