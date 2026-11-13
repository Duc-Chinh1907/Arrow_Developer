using Arrow.Business.Generation;
using Arrow.Common;
using Arrow.Common.Generation;
using Arrow.Common.Verification;

namespace Arrow.Business.Repair;

/// <summary>
/// Adaptive Repair (P04 §6.3). Keeps a checkpoint (the best version), a level 1–3, every code hash seen and the failed
/// attempts. A BETTER version becomes the checkpoint (back to level 1 when the state changes). A WORSE one is rolled back.
/// Worse, a repeated signature, duplicate code or an invalid output raise the level, and 3 goes back to 2 (Q5). An EQUAL
/// version with another signature keeps the level and is not a rollback.
/// </summary>
public sealed class AdaptiveRepairPolicy : IRepairPolicy
{
    private readonly string _cutFqn;
    private readonly int? _earlyStopP;
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly List<FailedAttempt> _attempts = [];
    private ChainVersion _best;
    private int _level = 1;
    private int _round;
    private int _noImprove;

    /// <param name="c0">The first candidate with its verification, which is not VALID.</param>
    /// <param name="cutFqn">The CUT.</param>
    /// <param name="earlyStopP"><c>early_stop_p</c>: null, or at least 3 (P04 §6.2).</param>
    public AdaptiveRepairPolicy(ChainVersion c0, string cutFqn, int? earlyStopP)
    {
        if (earlyStopP is < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(earlyStopP), earlyStopP, "early_stop_p phải ≥ 3 (P04 §6.2).");
        }

        _best = c0;
        _cutFqn = cutFqn;
        _earlyStopP = earlyStopP;
        if (c0.Candidate.CodeHash is { } hash)
        {
            _seen[hash] = c0.Round;
        }
    }

    /// <inheritdoc />
    public ChainVersion Best => _best;

    /// <inheritdoc />
    public RepairCounters Counters { get; private set; } = new();

    /// <inheritdoc />
    public StopReason? StopReason { get; private set; }

    /// <summary>The level asked for the next round.</summary>
    public int Level => _level;

    /// <summary>The failed attempts so far.</summary>
    public IReadOnlyList<FailedAttempt> Attempts => _attempts;

    /// <summary>Rounds in a row without improvement.</summary>
    public int NoImprove => _noImprove;

    /// <summary>
    /// Level actually used: 3 only when the checkpoint is COMPILE, RUNTIME or ASSERTION and has failing methods; otherwise 2
    /// (P04 §6.3, Q5 b).
    /// </summary>
    public static int EffectiveLevel(int requested, ChainVersion best) =>
        requested == 3 && best.State is VerificationState.Compile or VerificationState.Runtime or VerificationState.Assertion && best.Diagnosis.FailingMethods.Count > 0
            ? 3
            : Math.Min(requested, 2);

    /// <summary>Q5: 1 → 2, 2 → 3, 3 → 2.</summary>
    public static int NextLevel(int level) => level < 3 ? level + 1 : 2;

    /// <summary>The text of an invalid output in the list of failed attempts.</summary>
    public static string InvalidOutputText(string? reason) => "invalid output: " + reason switch
    {
        InvalidReasons.NoCode => "no Java class found",
        InvalidReasons.ParseError => "the code does not parse",
        InvalidReasons.MultipleClasses => "more than one top-level class",
        null or "" => "unknown",
        _ => reason,
    };

    /// <inheritdoc />
    public RepairRequest NextRequest()
    {
        var level = EffectiveLevel(_level, _best);
        return new RepairRequest(
            _cutFqn,
            _round + 1,
            level,
            _level,
            _best.State,
            _best.Code,
            _best.Round,
            _best.Diagnosis.ErrorBlock,
            null,
            level >= 2 ? [.. _attempts] : [],
            level == 3 ? [.. _best.Diagnosis.FailingMethods] : []);
    }

    /// <inheritdoc />
    public int? DuplicateOf(string? codeHash) => codeHash is not null && _seen.TryGetValue(codeHash, out var round) ? round : null;

    /// <inheritdoc />
    public RoundDecision Apply(RoundOutcome outcome)
    {
        var request = outcome.Request;
        var candidate = outcome.Candidate;
        _round = request.Round;
        var decision = new RoundDecision
        {
            Round = request.Round,
            BaseRound = request.BaseRound,
            RequestedLevel = request.RequestedLevel,
            Level = request.Level,
            Template = PromptBuilder.RepairUserPath(request.Level),
            CodeHash = candidate.ValidOutput ? candidate.CodeHash : null,
            DroppedMethods = [.. candidate.DroppedMethods],
        };
        if (request.Level == 3)
        {
            Counters = Counters with { Level3Rounds = Counters.Level3Rounds + 1 };
        }

        // An answer without usable code: a round that is not verified, and the level goes up.
        if (!candidate.ValidOutput)
        {
            _attempts.Add(new FailedAttempt(request.Round, InvalidOutputText(candidate.InvalidReason), FailureDiagnoser.Cut(candidate.ParseError, FailureDiagnoser.MaxFirstErrorChars), null));
            Counters = Counters with { InvalidOutput = Counters.InvalidOutput + 1 };
            return Finish(decision with { InvalidOutput = true }, improved: false, levelUp: true);
        }

        // Code seen before, rolled-back versions included: not verified, the level goes up.
        if (DuplicateOf(candidate.CodeHash) is { } same)
        {
            _attempts.Add(new FailedAttempt(request.Round, $"duplicate code (same as round {same})", null, LineDiff.Changes(_best.Code, candidate.Code ?? "")));
            Counters = Counters with { DuplicateCode = Counters.DuplicateCode + 1 };
            return Finish(decision with { DuplicateCode = true, DuplicateOfRound = same }, improved: false, levelUp: true);
        }

        var verify = outcome.Verify ?? throw new ArgumentException("A new valid candidate needs its verification.", nameof(outcome));
        if (verify.IsInfra)
        {
            throw new ArgumentException("An INFRA round stops the chain; it is not applied to the policy.", nameof(outcome));
        }

        if (candidate.CodeHash is { } hash)
        {
            _seen.TryAdd(hash, request.Round);
        }

        var comparison = CheckpointComparer.Compare(verify, _best.Verify);
        decision = decision with
        {
            State = verify.State,
            ErrorSignature = verify.ErrorSignature,
            TestsPassed = verify.TestsPassed,
            AssertionCount = verify.AssertionCount,
            Comparison = comparison,
        };

        // Better than the checkpoint (VALID always is): the new checkpoint; a new state starts again at level 1.
        if (comparison == Comparison.Better)
        {
            if (verify.State != _best.State)
            {
                _level = 1;
                Counters = Counters with { StateChange = Counters.StateChange + 1 };
            }

            var diagnosis = outcome.Diagnosis ?? new Diagnosis(verify.State!.Value, null, verify.ErrorSignature, candidate.CodeHash, [], "", null);
            _best = new ChainVersion(request.Round, candidate, verify, diagnosis);
            return Finish(decision with { Improved = true }, improved: true, levelUp: false);
        }

        // Not better: the next round repairs the checkpoint again. Worse (rollback) or the same signature raise the level.
        var repeated = verify.ErrorSignature is not null && verify.ErrorSignature == _best.Verify.ErrorSignature;
        var rollback = comparison == Comparison.Worse;
        _attempts.Add(new FailedAttempt(
            request.Round,
            verify.ErrorSignature ?? verify.State!.Value.ToString().ToUpperInvariant(),
            outcome.Diagnosis?.FirstError,
            LineDiff.Changes(_best.Code, candidate.Code ?? "")));
        if (rollback)
        {
            Counters = Counters with { Rollback = Counters.Rollback + 1 };
        }

        if (repeated)
        {
            Counters = Counters with { RepeatedSignature = Counters.RepeatedSignature + 1 };
        }

        return Finish(decision with { Rollback = rollback, RepeatedSignature = repeated }, improved: false, levelUp: rollback || repeated);
    }

    private RoundDecision Finish(RoundDecision decision, bool improved, bool levelUp)
    {
        if (levelUp)
        {
            _level = NextLevel(_level);
            Counters = Counters with { LevelUp = Counters.LevelUp + 1 };
        }

        _noImprove = improved ? 0 : _noImprove + 1;
        return decision with { LevelAfter = _level, CheckpointRound = _best.Round, NoImprove = _noImprove };
    }

    /// <inheritdoc />
    /// <remarks>Stagnation is checked first, as in the pseudo-code of P04 §6.3, where it is checked inside the loop.</remarks>
    public bool CanContinue(int round, int n)
    {
        if (_earlyStopP is { } p && _noImprove >= p)
        {
            StopReason = Common.StopReason.Stagnation;
            return false;
        }

        if (round >= n)
        {
            StopReason = Common.StopReason.Budget;
            return false;
        }

        return true;
    }
}
