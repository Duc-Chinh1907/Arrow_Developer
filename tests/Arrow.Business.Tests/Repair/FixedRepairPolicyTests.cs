using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Generation;
using Arrow.Common.Verification;

namespace Arrow.Business.Tests.Repair;

/// <summary>The fixed branches of TN2 (P04 §6.7): the Fixed column of R03 §7 and N6, F+, F− and F2.</summary>
public class FixedRepairPolicyTests
{
    private const string Cut = "a.X";

    private static VerifyResult V(VerificationState state, string? signature = null, int passed = 0) => new()
    {
        State = state,
        ErrorSignature = signature ?? (state == VerificationState.Valid ? null : $"{state.ToString().ToUpperInvariant()} | x | -"),
        TestMethods = new TestMethodCounts(5, passed, 5 - passed),
        TestsPassed = passed,
    };

    private static CandidateTest C(string code) =>
        new(true, null, null, code, "a.X_ArrowTest", "src/test/java/a/X_ArrowTest.java", "sha256:" + code, 1, 1, [], [], "stop");

    private static readonly CandidateTest Invalid = new(false, InvalidReasons.NoCode, null, null, "a.X_ArrowTest", "src/test/java/a/X_ArrowTest.java", null, 0, 0, [], [], "stop");

    private static Diagnosis D(VerifyResult v) =>
        new(v.State!.Value, StateGroups.GroupOf(v.State), v.ErrorSignature, null, [], $"error of {v.ErrorSignature}", $"first error of {v.ErrorSignature}");

    private static IRepairPolicy Policy(string mechanism, VerifyResult c0) =>
        RepairPolicies.Create(mechanism, new ChainVersion(0, C("c0"), c0, D(c0)), Cut, 3);

    /// <summary>One round as the runner does it: the request, the duplicate check, then the outcome.</summary>
    private static (RepairRequest Request, RoundDecision Decision) Round(IRepairPolicy policy, CandidateTest candidate, VerifyResult? verify)
    {
        var request = policy.NextRequest();
        var built = candidate.ValidOutput && policy.DuplicateOf(candidate.CodeHash) is null;
        var decision = policy.Apply(new RoundOutcome(request, candidate, built ? verify : null, built && verify is not null && verify.State != VerificationState.Valid ? D(verify) : null));
        return (request, decision);
    }

    [Theory]
    [InlineData(RepairMechanisms.Adaptive, typeof(AdaptiveRepairPolicy))]
    [InlineData(RepairMechanisms.FixedPlus, typeof(FixedRepairPolicy))]
    [InlineData(RepairMechanisms.FixedMinus, typeof(FixedRepairPolicy))]
    [InlineData(RepairMechanisms.FixedLevel2, typeof(FixedRepairPolicy))]
    public void EachMechanismHasItsPolicy(string mechanism, Type type) =>
        Assert.IsType(type, Policy(mechanism, V(VerificationState.Compile)));

    [Fact]
    public void AnUnknownMechanismIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy("fixed", V(VerificationState.Compile)));

    // R03 §7, Fixed column: always from the latest version, worse or not; duplicate code is verified again.
    [Fact]
    public void Section7TableFixed()
    {
        var policy = Policy(RepairMechanisms.FixedPlus, V(VerificationState.Compile, "COMPILE | cannot find symbol | method"));

        var (req1, d1) = Round(policy, C("r1"), V(VerificationState.Runtime));
        Assert.Equal((1, 1, 1, 0, VerificationState.Compile, "c0"), (req1.Round, req1.Level, req1.RequestedLevel, req1.BaseRound, req1.State, req1.BaseCode));
        Assert.Equal((RepairMechanisms.FixedPlus, RepairMechanisms.FixedPlus), (req1.Mechanism, d1.Mechanism));
        Assert.Equal(("repair/user/level1.md", 1, 1, false), (d1.Template, d1.LevelAfter, d1.CheckpointRound, d1.Improved));
        Assert.Null(d1.Comparison);

        // 2: COMPILE, worse, is still the version repaired next (N6: Fixed never rolls back).
        var (req2, d2) = Round(policy, C("r2"), V(VerificationState.Compile, "COMPILE | incompatible types | -"));
        Assert.Equal((1, VerificationState.Runtime, "r1"), (req2.BaseRound, req2.State, req2.BaseCode));
        Assert.Equal((false, 2), (d2.Rollback, d2.CheckpointRound));

        // 3: same code as r2 is verified again; its first round is only recorded.
        var (req3, d3) = Round(policy, C("r2"), V(VerificationState.Compile, "COMPILE | incompatible types | -"));
        Assert.Equal((2, "r2"), (req3.BaseRound, req3.BaseCode));
        Assert.Equal((false, 2, VerificationState.Compile, 3), (d3.DuplicateCode, d3.DuplicateOfRound, d3.State, d3.CheckpointRound));
        Assert.Empty(req3.FailedAttempts);

        var (_, d4) = Round(policy, C("r4"), V(VerificationState.Assertion, "ASSERTION | AssertionError | t1", passed: 4));
        Assert.Equal(4, d4.CheckpointRound);

        // 5: fewer tests pass than r4, still the latest.
        Round(policy, C("r5"), V(VerificationState.Assertion, "ASSERTION | AssertionError | t1", passed: 3));
        Assert.Equal((5, 3), (policy.Best.Round, policy.Best.Verify.TestsPassed));

        var (req6, d6) = Round(policy, C("r6"), V(VerificationState.Valid, passed: 5));
        Assert.Equal((1, 5), (req6.Level, req6.BaseRound));
        Assert.True(d6.Improved);
        Assert.Equal((VerificationState.Valid, 6), (policy.Best.State, policy.Best.Round));
        Assert.Equal(new RepairCounters(DuplicateCode: 1, StateChange: 4), policy.Counters);
    }

    // R03 §6.6: an invalid output counts as a round and the latest version stays.
    [Fact]
    public void AnInvalidOutputKeepsTheLatestVersion()
    {
        var policy = Policy(RepairMechanisms.FixedPlus, V(VerificationState.Compile));
        Round(policy, C("r1"), V(VerificationState.Runtime));
        var (_, d2) = Round(policy, Invalid, null);
        Assert.Equal((true, 1, (VerificationState?)null), (d2.InvalidOutput, d2.CheckpointRound, d2.State));
        var (req3, _) = Round(policy, C("r3"), V(VerificationState.Assertion));
        Assert.Equal((3, 1, "r1"), (req3.Round, req3.BaseRound, req3.BaseCode));
        Assert.Equal(1, policy.Counters.InvalidOutput);
    }

    // R03 §6.2: Fixed does not stop early, even when nothing changes for longer than early_stop_p.
    [Fact]
    public void FixedRunsTheWholeBudget()
    {
        var policy = Policy(RepairMechanisms.FixedLevel2, V(VerificationState.Compile));
        for (var round = 1; round <= 4; round++)
        {
            Round(policy, C("same"), V(VerificationState.Compile));
            Assert.True(policy.CanContinue(round, 5));
        }

        Round(policy, C("same"), V(VerificationState.Compile));
        Assert.False(policy.CanContinue(5, 5));
        Assert.Equal(StopReason.Budget, policy.StopReason);
    }

    // F−: the same requests as F+; only the prompt differs (PromptTests).
    [Fact]
    public void FixedMinusAsksForLevelOne()
    {
        var policy = Policy(RepairMechanisms.FixedMinus, V(VerificationState.Compile));
        var (req, d) = Round(policy, C("r1"), V(VerificationState.Runtime));
        Assert.Equal((1, RepairMechanisms.FixedMinus, "repair/user/level1.md"), (req.Level, req.Mechanism, d.Template));
        Assert.Empty(policy.NextRequest().FailedAttempts);
    }

    // F2: level 2 every round; failed_attempts has every earlier round, with its signature, first error and changed code.
    [Fact]
    public void FixedLevel2ShowsEveryEarlierRound()
    {
        var policy = Policy(RepairMechanisms.FixedLevel2, V(VerificationState.Compile));
        var (req1, d1) = Round(policy, C("r1"), V(VerificationState.Runtime, "RUNTIME | NullPointerException | A.b"));
        Assert.Equal((2, 2, "repair/user/level2.md", 2), (req1.Level, req1.RequestedLevel, d1.Template, d1.LevelAfter));
        Assert.Empty(req1.FailedAttempts);

        Round(policy, Invalid, null);
        var (req3, _) = Round(policy, C("r3"), V(VerificationState.Compile, "COMPILE | incompatible types | -"));
        Assert.Equal([1, 2], req3.FailedAttempts.Select(a => a.Round));
        Assert.Equal("RUNTIME | NullPointerException | A.b", req3.FailedAttempts[0].Signature);
        Assert.Equal("first error of RUNTIME | NullPointerException | A.b", req3.FailedAttempts[0].FirstError);
        Assert.Contains("+r1", req3.FailedAttempts[0].Change, StringComparison.Ordinal);
        Assert.Equal("invalid output: no Java class found", req3.FailedAttempts[1].Signature);
        Assert.Equal((1, "r1"), (req3.BaseRound, req3.BaseCode));

        // The worse round 3 is repaired next, and is in the list too.
        var req4 = policy.NextRequest();
        Assert.Equal((3, 3), (req4.BaseRound, req4.FailedAttempts.Count));
        Assert.Equal("COMPILE | incompatible types | -", req4.FailedAttempts[2].Signature);
    }
}
