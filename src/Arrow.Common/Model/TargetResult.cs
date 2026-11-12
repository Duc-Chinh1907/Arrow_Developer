using Arrow.Common.Errors;

namespace Arrow.Common;

/// <summary>
/// Result of one CUT: the content of <c>targets/&lt;cut_fqn&gt;/result.json</c> (P07 §6).
/// <c>arrow show</c>, <c>--json</c> and the end-of-run table read it.
/// </summary>
/// <param name="CutFqn">The CUT.</param>
/// <param name="Result">Final result.</param>
/// <param name="C0State">State of the first candidate; null when there was no c0 (blocked, invalid output).</param>
/// <param name="RoundsRun">Repair rounds run; 0 when c0 was VALID or repair was off.</param>
/// <param name="SuccessRound">Round that reached VALID (0 for c0); null when the suite never became VALID.</param>
/// <param name="StopReason">Why the chain stopped; null when no chain ran.</param>
/// <param name="TestFqn">FQN of the generated test class.</param>
/// <param name="TestPath">Path of the generated test in the project.</param>
/// <param name="FinalCode">Final suite, relative to the target folder in <c>runs/</c>, e.g. <c>block3/final.java</c>.</param>
/// <param name="Metrics">Quality metrics; null when block 4 did not run.</param>
/// <param name="MetricsSkipped"><c>"disabled"</c> when the user turned measuring off (P05 §2); otherwise null.</param>
/// <param name="Tools">Status of each quality tool (<c>jacoco</c>, <c>pit</c>, <c>tsdetect</c>); null when block 4 did not run.</param>
/// <param name="Tokens">Model tokens used for this CUT.</param>
/// <param name="CostUsd">Estimated cost; null when the price of the model is unknown.</param>
/// <param name="LlmCalls">Number of model calls, replayed ones included.</param>
/// <param name="Seconds">Time spent per block.</param>
/// <param name="Error">Error behind <c>BLOCKED</c>, <c>INFRA</c>, <c>INVALID_OUTPUT</c> or <c>CANCELLED</c>; otherwise null.</param>
/// <param name="Warnings">Short warning tags, e.g. <c>dropped_methods: 1</c>.</param>
/// <param name="Apply">What happened when writing the test into the project; null until P06 ran.</param>
public sealed record TargetResult(
    string CutFqn,
    TargetOutcome Result,
    VerificationState? C0State,
    int RoundsRun,
    int? SuccessRound,
    StopReason? StopReason,
    string? TestFqn,
    string? TestPath,
    string? FinalCode,
    TargetMetrics? Metrics,
    string? MetricsSkipped,
    IReadOnlyDictionary<string, ToolStatus>? Tools,
    TokenUsage Tokens,
    double? CostUsd,
    int LlmCalls,
    BlockSeconds Seconds,
    ErrorInfo? Error,
    IReadOnlyList<string> Warnings,
    ApplySummary? Apply);

/// <summary>Quality metrics of the final suite (P05). Ratios are 0–1.</summary>
public sealed record TargetMetrics(
    double? Line,
    double? Branch,
    double? Method,
    double? Mutation,
    double? TestStrength,
    double? SmellDensity);

/// <summary>Model token counts. JSON: <c>{ "in": …, "out": … }</c>.</summary>
public sealed record TokenUsage(long In, long Out)
{
    public static TokenUsage Zero { get; } = new(0, 0);
}

/// <summary>Seconds spent per block; a block that did not run is null.</summary>
public sealed record BlockSeconds(
    double? Block1,
    double? Block2,
    double? Block3,
    double? Block4,
    double Total);

/// <summary>Short form of <c>apply.json</c> kept in <c>result.json</c> (P06 §6).</summary>
public sealed record ApplySummary(ApplyDecision Decision, string? Path);
