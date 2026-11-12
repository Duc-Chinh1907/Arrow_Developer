namespace Arrow.Common.Events;

/// <summary>
/// Progress event sent by use cases through <c>IProgress&lt;ArrowEvent&gt;</c> and written to <c>events.jsonl</c> (P07 §10).
/// Events are raised on worker threads; the CLI moves them to its display thread and must not block.
/// </summary>
/// <param name="At">When the event happened (UTC).</param>
/// <param name="Type">One of <see cref="ArrowEventTypes"/>, e.g. <c>B3.Round</c>.</param>
/// <param name="CutFqn">The target the event belongs to; null for run-level events.</param>
/// <param name="Index">1-based position of the target in the run, shown as <c>[2/5]</c>.</param>
/// <param name="Total">Number of targets in the run.</param>
/// <param name="Level">Severity.</param>
/// <param name="Message">Short sentence in the UI language, for plain output.</param>
/// <param name="Data">Structured details for rich and JSON output.</param>
public sealed record ArrowEvent(
    DateTimeOffset At,
    string Type,
    string? CutFqn,
    int? Index,
    int? Total,
    EventLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Data);

/// <summary>Event types of P07 §10, grouped by the spec that raises them.</summary>
public static class ArrowEventTypes
{
    // Run (P07)
    public const string RunStarted = "RUN.Started";
    public const string RunTargetsResolved = "RUN.TargetsResolved";
    public const string RunEstimate = "RUN.Estimate";
    public const string RunTargetStarted = "RUN.TargetStarted";
    public const string RunTargetDone = "RUN.TargetDone";
    public const string RunStopping = "RUN.Stopping";
    public const string RunFinished = "RUN.Finished";

    // Block 1 (P01 §8)
    public const string B1Checkout = "B1.Checkout";
    public const string B1Locate = "B1.Locate";
    public const string B1Plan = "B1.Plan";
    public const string B1Compile = "B1.Compile";
    public const string B1Baseline = "B1.Baseline";
    public const string B1Done = "B1.Done";
    public const string B1Blocked = "B1.Blocked";

    // Block 2 (P02 §8)
    public const string B2Prompt = "B2.Prompt";
    public const string B2SourceTruncated = "B2.SourceTruncated";
    public const string B2Calling = "B2.Calling";
    public const string B2Response = "B2.Response";
    public const string B2Candidate = "B2.Candidate";
    public const string B2Truncated = "B2.Truncated";

    // LLM (P03 §12)
    public const string LlmRetry = "LLM.Retry";
    public const string LlmReplayed = "LLM.Replayed";
    public const string LlmBudgetWarning = "LLM.BudgetWarning";

    // Block 3 (P04 §9)
    public const string B3VerifyStep = "B3.VerifyStep";
    public const string B3Verified = "B3.Verified";
    public const string B3InfraRetry = "B3.InfraRetry";
    public const string B3Round = "B3.Round";
    public const string B3Final = "B3.Final";

    // Block 4 (P05 §9)
    public const string B4ToolStart = "B4.ToolStart";
    public const string B4ToolDone = "B4.ToolDone";
    public const string B4ToolFailed = "B4.ToolFailed";
    public const string B4Done = "B4.Done";

    // Writing into the project (P06 §8)
    public const string ApplyPlanned = "APPLY.Planned";
    public const string ApplyRenamed = "APPLY.Renamed";
    public const string ApplyReverified = "APPLY.Reverified";
    public const string ApplyWritten = "APPLY.Written";
    public const string ApplySkipped = "APPLY.Skipped";
}
