using Arrow.Business.Generation;
using Arrow.Common;
using Arrow.Common.Generation;
using Arrow.Common.Verification;

namespace Arrow.Business.Repair;

/// <summary>Chooses the policy of a chain from <c>repair.mechanism</c> (P04 §6.7).</summary>
public static class RepairPolicies
{
    /// <summary>The policy of <paramref name="mechanism"/> for a chain starting at <paramref name="c0"/>.</summary>
    /// <param name="mechanism">One of <see cref="RepairMechanisms.All"/>.</param>
    /// <param name="c0">The first candidate with its verification, which is not VALID.</param>
    /// <param name="cutFqn">The CUT.</param>
    /// <param name="earlyStopP"><c>early_stop_p</c>; only Adaptive uses it.</param>
    public static IRepairPolicy Create(string mechanism, ChainVersion c0, string cutFqn, int? earlyStopP) => mechanism switch
    {
        RepairMechanisms.Adaptive => new AdaptiveRepairPolicy(c0, cutFqn, earlyStopP),
        RepairMechanisms.FixedPlus or RepairMechanisms.FixedMinus or RepairMechanisms.FixedLevel2 => new FixedRepairPolicy(c0, cutFqn, mechanism),
        _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "repair.mechanism: " + string.Join(", ", RepairMechanisms.All)),
    };
}

/// <summary>
/// The fixed branches of TN2 (R03 §6.6, P04 §6.7): every round repairs the latest version, worse or not, and the chain ends
/// with the latest version. No checkpoint, rollback, level change or early stop; code seen before is verified again (its first
/// round is kept in <see cref="RoundDecision.DuplicateOfRound"/> so that duplicates can be counted). F+ and F− use the level 1
/// prompt (F− without the repository configuration); F2 uses the level 2 prompt with <c>extra_context</c> and every earlier
/// round in <c>failed_attempts</c>.
/// </summary>
public sealed class FixedRepairPolicy : IRepairPolicy
{
    private readonly string _cutFqn;
    private readonly string _mechanism;
    private readonly int _level;
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly List<FailedAttempt> _attempts = [];
    private ChainVersion _latest;
    private int _round;

    /// <param name="c0">The first candidate with its verification, which is not VALID.</param>
    /// <param name="cutFqn">The CUT.</param>
    /// <param name="mechanism"><c>fixed_plus</c>, <c>fixed_minus</c> or <c>fixed_l2</c>.</param>
    public FixedRepairPolicy(ChainVersion c0, string cutFqn, string mechanism)
    {
        if (!RepairMechanisms.IsFixed(mechanism))
        {
            throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "Not a fixed mechanism.");
        }

        _latest = c0;
        _cutFqn = cutFqn;
        _mechanism = mechanism;
        _level = mechanism == RepairMechanisms.FixedLevel2 ? 2 : 1;
        if (c0.Candidate.CodeHash is { } hash)
        {
            _seen[hash] = c0.Round;
        }
    }

    /// <inheritdoc />
    /// <remarks>The latest version: Fixed repairs it and ends with it.</remarks>
    public ChainVersion Best => _latest;

    /// <inheritdoc />
    public RepairCounters Counters { get; private set; } = new();

    /// <inheritdoc />
    public StopReason? StopReason { get; private set; }

    /// <summary>The failed attempts so far; F2 shows them to the model.</summary>
    public IReadOnlyList<FailedAttempt> Attempts => _attempts;

    /// <inheritdoc />
    public RepairRequest NextRequest() => new(
        _cutFqn,
        _round + 1,
        _level,
        _level,
        _latest.State,
        _latest.Code,
        _latest.Round,
        _latest.Diagnosis.ErrorBlock,
        null,
        _level >= 2 ? [.. _attempts] : [],
        [],
        _mechanism);

    /// <inheritdoc />
    /// <remarks>Always null: Fixed verifies code it has seen before (R03 §6.6).</remarks>
    public int? DuplicateOf(string? codeHash) => null;

    /// <inheritdoc />
    public RoundDecision Apply(RoundOutcome outcome)
    {
        var request = outcome.Request;
        var candidate = outcome.Candidate;
        _round = request.Round;
        var decision = new RoundDecision
        {
            Round = request.Round,
            Mechanism = _mechanism,
            BaseRound = request.BaseRound,
            RequestedLevel = request.RequestedLevel,
            Level = request.Level,
            Template = PromptBuilder.RepairUserPath(request.Level),
            CodeHash = candidate.ValidOutput ? candidate.CodeHash : null,
            DroppedMethods = [.. candidate.DroppedMethods],
            LevelAfter = _level,
        };

        // An answer without usable code: one round, the latest version stays.
        if (!candidate.ValidOutput)
        {
            _attempts.Add(new FailedAttempt(request.Round, AdaptiveRepairPolicy.InvalidOutputText(candidate.InvalidReason), FailureDiagnoser.Cut(candidate.ParseError, FailureDiagnoser.MaxFirstErrorChars), null));
            Counters = Counters with { InvalidOutput = Counters.InvalidOutput + 1 };
            return decision with { InvalidOutput = true, CheckpointRound = _latest.Round };
        }

        var verify = outcome.Verify ?? throw new ArgumentException("A valid candidate needs its verification.", nameof(outcome));
        if (verify.IsInfra)
        {
            throw new ArgumentException("An INFRA round stops the chain; it is not applied to the policy.", nameof(outcome));
        }

        // Code seen before is verified again; the round that saw it first is only recorded.
        int? duplicateOf = null;
        if (candidate.CodeHash is { } hash)
        {
            if (_seen.TryGetValue(hash, out var first))
            {
                duplicateOf = first;
                Counters = Counters with { DuplicateCode = Counters.DuplicateCode + 1 };
            }
            else
            {
                _seen[hash] = request.Round;
            }
        }

        if (verify.State != _latest.State)
        {
            Counters = Counters with { StateChange = Counters.StateChange + 1 };
        }

        if (verify.State != VerificationState.Valid)
        {
            _attempts.Add(new FailedAttempt(
                request.Round,
                verify.ErrorSignature ?? verify.State!.Value.ToString().ToUpperInvariant(),
                outcome.Diagnosis?.FirstError,
                LineDiff.Changes(_latest.Code, candidate.Code ?? "")));
        }

        var diagnosis = outcome.Diagnosis ?? new Diagnosis(verify.State!.Value, null, verify.ErrorSignature, candidate.CodeHash, [], "", null);
        _latest = new ChainVersion(request.Round, candidate, verify, diagnosis);
        return decision with
        {
            State = verify.State,
            ErrorSignature = verify.ErrorSignature,
            TestsPassed = verify.TestsPassed,
            AssertionCount = verify.AssertionCount,
            DuplicateOfRound = duplicateOf,
            Improved = verify.State == VerificationState.Valid,
            CheckpointRound = request.Round,
        };
    }

    /// <inheritdoc />
    /// <remarks>Only the budget: Fixed does not stop early (R03 §6.2).</remarks>
    public bool CanContinue(int round, int n)
    {
        if (round >= n)
        {
            StopReason = Common.StopReason.Budget;
            return false;
        }

        return true;
    }
}
