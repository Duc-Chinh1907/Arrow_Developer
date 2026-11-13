using System.Text.Json.Serialization;
using Arrow.Common;
using Arrow.Common.Generation;
using Arrow.Common.Verification;

namespace Arrow.Business.Repair;

/// <summary>A version of the test in the chain (c0 is round 0) with its verification and diagnosis.</summary>
/// <param name="Round">Round that made it; 0 for c0.</param>
/// <param name="Candidate">The candidate.</param>
/// <param name="Verify">Its verification, never INFRA.</param>
/// <param name="Diagnosis">Its diagnosis.</param>
public sealed record ChainVersion(int Round, CandidateTest Candidate, VerifyResult Verify, Diagnosis Diagnosis)
{
    /// <summary>The code; empty when the candidate has none.</summary>
    public string Code => Candidate.Code ?? "";

    /// <summary>The verification state.</summary>
    public VerificationState State => Verify.State ?? throw new InvalidOperationException("An INFRA result cannot be a version of the chain.");
}

/// <summary>What happened in one round, given to <see cref="IRepairPolicy.Apply"/>.</summary>
/// <param name="Request">The request of the round, from <see cref="IRepairPolicy.NextRequest"/>.</param>
/// <param name="Candidate">The answer of block 2.</param>
/// <param name="Verify">Its verification; null when it was not verified (invalid output or duplicate code).</param>
/// <param name="Diagnosis">Its diagnosis; null when it was not verified, or when it is VALID.</param>
public sealed record RoundOutcome(RepairRequest Request, CandidateTest Candidate, VerifyResult? Verify = null, Diagnosis? Diagnosis = null);

/// <summary>Counters of a chain, the <c>counters</c> of <c>final.json</c> (P04 §7).</summary>
public sealed record RepairCounters(
    int Rollback = 0,
    int DuplicateCode = 0,
    int InvalidOutput = 0,
    int RepeatedSignature = 0,
    int LevelUp = 0,
    int StateChange = 0,
    [property: JsonPropertyName("level3_rounds")] int Level3Rounds = 0);

/// <summary>
/// <c>decision.json</c> of a round (P04 §6.6). The policy fills the decision; the runner adds the measurements
/// (<c>extra_context_warnings</c>, <c>llm_*</c>, <c>build_seconds</c>, <c>replayed</c>). The fields of the verification are null
/// when the round was not verified.
/// </summary>
public sealed record RoundDecision
{
    /// <summary>The round, from 1.</summary>
    public int Round { get; init; }

    /// <summary>Round of the checkpoint that was repaired.</summary>
    public int BaseRound { get; init; }

    /// <summary>Level the policy asked for.</summary>
    public int RequestedLevel { get; init; }

    /// <summary>Level used: 3 becomes 2 when level 3 does not apply.</summary>
    public int Level { get; init; }

    /// <summary>User template of the level.</summary>
    public string Template { get; init; } = "";

    /// <summary>State of the new version; null when not verified.</summary>
    public VerificationState? State { get; init; }

    /// <summary>Its signature; null when not verified or VALID.</summary>
    public string? ErrorSignature { get; init; }

    /// <summary>Hash of the new code; null for an invalid output.</summary>
    public string? CodeHash { get; init; }

    /// <summary>The code was seen before, so it was not verified.</summary>
    public bool DuplicateCode { get; init; }

    /// <summary>Round of the same code; 0 for c0.</summary>
    public int? DuplicateOfRound { get; init; }

    /// <summary>The answer had no usable code.</summary>
    public bool InvalidOutput { get; init; }

    /// <summary>The new version against the checkpoint; null when not verified.</summary>
    public Comparison? Comparison { get; init; }

    /// <summary>The new version was worse, so the chain went back to the checkpoint.</summary>
    public bool Rollback { get; init; }

    /// <summary>The new version has the signature of the checkpoint.</summary>
    public bool RepeatedSignature { get; init; }

    /// <summary>The new version became the checkpoint.</summary>
    public bool Improved { get; init; }

    /// <summary>Level for the next round.</summary>
    public int LevelAfter { get; init; }

    /// <summary>Round of the checkpoint after this round.</summary>
    public int CheckpointRound { get; init; }

    /// <summary>Rounds in a row without improvement, this one included.</summary>
    public int NoImprove { get; init; }

    /// <summary>Methods of the new version that passed; null when not verified.</summary>
    public int? TestsPassed { get; init; }

    /// <summary>Assertions of the new version; null when not verified.</summary>
    public int? AssertionCount { get; init; }

    /// <summary>Methods level 3 did not take from the answer (P02).</summary>
    public IReadOnlyList<string> DroppedMethods { get; init; } = [];

    /// <summary>Parts of <c>extra_context</c> that could not be built (P04 §6.5).</summary>
    public IReadOnlyList<string> ExtraContextWarnings { get; init; } = [];

    /// <summary>Time of the model call.</summary>
    public double LlmSeconds { get; init; }

    /// <summary>Time of the verification.</summary>
    public double BuildSeconds { get; init; }

    /// <summary>Tokens of the model call.</summary>
    public TokenUsage LlmTokens { get; init; } = TokenUsage.Zero;

    /// <summary>Model calls of the round minus 1 (R03 §6.7).</summary>
    public int LlmRetries { get; init; }

    /// <summary>The answer came from the cache.</summary>
    public bool Replayed { get; init; }
}

/// <summary>A repair algorithm (P04 §11). Pure code: it calls no model and builds nothing.</summary>
public interface IRepairPolicy
{
    /// <summary>The request of the next round; <c>extra_context</c> is null, the caller fills it at level 2 and 3.</summary>
    RepairRequest NextRequest();

    /// <summary>Round that saw <paramref name="codeHash"/> first (0 for c0); null when it is new. Such code is not verified.</summary>
    int? DuplicateOf(string? codeHash);

    /// <summary>Updates the checkpoint, level, hashes, attempts and the stagnation counter after a round.</summary>
    RoundDecision Apply(RoundOutcome outcome);

    /// <summary>
    /// Whether another round may run after <paramref name="round"/> rounds of at most <paramref name="n"/>; when not,
    /// <see cref="StopReason"/> says why (<c>stagnation</c> or <c>budget</c>).
    /// </summary>
    bool CanContinue(int round, int n);

    /// <summary>Why <see cref="CanContinue"/> returned false; null before.</summary>
    StopReason? StopReason { get; }

    /// <summary>The checkpoint: the best version so far.</summary>
    ChainVersion Best { get; }

    /// <summary>The counters so far.</summary>
    RepairCounters Counters { get; }
}
