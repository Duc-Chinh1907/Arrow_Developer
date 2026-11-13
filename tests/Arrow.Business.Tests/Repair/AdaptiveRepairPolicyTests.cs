using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Generation;
using Arrow.Common.Json;
using Arrow.Common.Verification;

namespace Arrow.Business.Tests.Repair;

/// <summary>Adaptive Repair with the examples of P04 §6 and §12 (R03 §7, §14.5, §15; Q5). Parity with RepairPolicyTests of ARROW_Framework.</summary>
public class AdaptiveRepairPolicyTests
{
    private const string Cut = "a.X";

    private static VerifyResult V(VerificationState state, string? signature = null, int passed = 0, int assertions = 0, params string[] failing) => new()
    {
        State = state,
        ErrorSignature = signature ?? (state == VerificationState.Valid ? null : $"{state.ToString().ToUpperInvariant()} | x | -"),
        TestMethods = new TestMethodCounts(5, passed, 5 - passed),
        TestsPassed = passed,
        AssertionCount = assertions,
        FailingMethods = [.. failing],
    };

    private static CandidateTest C(string code) =>
        new(true, null, null, code, "a.X_ArrowTest", "src/test/java/a/X_ArrowTest.java", "sha256:" + code, 1, 1, [], [], "stop");

    private static readonly CandidateTest Invalid = new(false, InvalidReasons.NoCode, null, null, "a.X_ArrowTest", "src/test/java/a/X_ArrowTest.java", null, 0, 0, [], [], "stop");

    private static Diagnosis D(VerifyResult v) =>
        new(v.State!.Value, StateGroups.GroupOf(v.State), v.ErrorSignature, null, [.. v.FailingMethods], $"error of {v.ErrorSignature}", $"first error of {v.ErrorSignature}");

    private static ChainVersion Version(int round, string code, VerifyResult v) => new(round, C(code), v, D(v));

    private static AdaptiveRepairPolicy Policy(VerifyResult c0, string code = "c0", int? p = null) => new(Version(0, code, c0), Cut, p);

    /// <summary>One round as the runner does it: the request, the duplicate check, then the outcome.</summary>
    private static (RepairRequest Request, RoundDecision Decision) Round(AdaptiveRepairPolicy policy, CandidateTest candidate, VerifyResult? verify)
    {
        var request = policy.NextRequest();
        var built = candidate.ValidOutput && policy.DuplicateOf(candidate.CodeHash) is null;
        var decision = policy.Apply(new RoundOutcome(request, candidate, built ? verify : null, built && verify is not null ? D(verify) : null));
        return (request, decision);
    }

    // Parity: RepairPolicyTests.Section7TableAdaptive (P04 §12 item 1)
    [Fact]
    public void Section7Table()
    {
        var policy = Policy(V(VerificationState.Compile, "COMPILE | cannot find symbol | method"));

        // 1: RUNTIME → better, new state → checkpoint r1, level 1.
        var (req1, d1) = Round(policy, C("r1"), V(VerificationState.Runtime, "RUNTIME | NullPointerException | A.b"));
        Assert.Equal((1, 1, 0, VerificationState.Compile), (req1.Round, req1.Level, req1.BaseRound, req1.State));
        Assert.Equal((Comparison.Better, true, 1, 1), (d1.Comparison, d1.Improved, d1.LevelAfter, d1.CheckpointRound));

        // 2: COMPILE → worse → rollback, attempts += r2, level 2.
        var (req2, d2) = Round(policy, C("r2"), V(VerificationState.Compile, "COMPILE | incompatible types | -"));
        Assert.Equal((1, 1, VerificationState.Runtime), (req2.Level, req2.BaseRound, req2.State));
        Assert.Equal((Comparison.Worse, true, 2, 1), (d2.Comparison, d2.Rollback, d2.LevelAfter, d2.CheckpointRound));

        // 3: same code as r2 → not verified, attempts += r3, level 3.
        var (req3, d3) = Round(policy, C("r2"), null);
        Assert.Equal(2, req3.Level);
        Assert.Single(req3.FailedAttempts);
        Assert.Equal((true, 2, (VerificationState?)null, (Comparison?)null, 3), (d3.DuplicateCode, d3.DuplicateOfRound!.Value, d3.State, d3.Comparison, d3.LevelAfter));
        Assert.Equal("duplicate code (same as round 2)", policy.Attempts[^1].Signature);

        // 4: ASSERTION → better → checkpoint r4, level 1. The RUNTIME checkpoint has no failing method, so 3 became 2.
        var (req4, d4) = Round(policy, C("r4"), V(VerificationState.Assertion, "ASSERTION | AssertionError | t1", passed: 4, failing: "t1"));
        Assert.Equal((3, 2), (req4.RequestedLevel, req4.Level));
        Assert.Equal("repair/user/level2.md", d4.Template);
        Assert.Equal((true, 1, 4), (d4.Improved, d4.LevelAfter, d4.CheckpointRound));

        // 5: ASSERTION, same signature, fewer tests pass → worse and repeated → level 2 (one step).
        var (_, d5) = Round(policy, C("r5"), V(VerificationState.Assertion, "ASSERTION | AssertionError | t1", passed: 3, failing: "t1"));
        Assert.Equal((true, true, 2, 4), (d5.Rollback, d5.RepeatedSignature, d5.LevelAfter, d5.CheckpointRound));

        // 6: VALID → success in round 6.
        var (req6, d6) = Round(policy, C("r6"), V(VerificationState.Valid, passed: 5));
        Assert.Equal((2, 4), (req6.Level, req6.BaseRound));
        Assert.Equal(3, req6.FailedAttempts.Count);
        Assert.True(d6.Improved);
        Assert.Equal((VerificationState.Valid, 6), (policy.Best.State, policy.Best.Round));
        Assert.Equal(new RepairCounters(Rollback: 2, DuplicateCode: 1, RepeatedSignature: 1, LevelUp: 3, StateChange: 3), policy.Counters);
    }

    // Parity: RepairPolicyTests.Section14_5AdaptiveChain (P04 §12 item 3: O8 → O3 → O4 → O7 → O1, success in round 5)
    [Fact]
    public void Section14_5Chain()
    {
        const string getTotal = "COMPILE | cannot find symbol | method";
        var policy = Policy(V(VerificationState.Compile, getTotal, failing: "totalPriceAddsTax"));

        // O8, round 1: COMPILE, another error → EQUAL, other signature → keep level 1, no rollback.
        var (_, d1) = Round(policy, C("r1"), V(VerificationState.Compile, "COMPILE | constructor cannot be applied | -", failing: "totalPriceAddsTax"));
        Assert.Equal((Comparison.Equal, false, false, 1), (d1.Comparison, d1.Rollback, d1.RepeatedSignature, d1.LevelAfter));

        // O3, round 2: same prompt, the model gives round 1 again → duplicate → level 2.
        var (req2, d2) = Round(policy, C("r1"), null);
        Assert.Equal(1, req2.Level);
        Assert.Equal((true, 2), (d2.DuplicateCode, d2.LevelAfter));

        // O4, round 3: level 2 (real API and attempts of rounds 1, 2) → ASSERTION → checkpoint, level 1.
        var (req3, d3) = Round(policy, C("r3"), V(VerificationState.Assertion, "ASSERTION | AssertionError | totalPriceAddsTax", passed: 4, assertions: 7, failing: "totalPriceAddsTax"));
        Assert.Equal(2, req3.Level);
        Assert.Equal(["COMPILE | constructor cannot be applied | -", "duplicate code (same as round 1)"], req3.FailedAttempts.Select(a => a.Signature));
        Assert.Equal((true, 1, 3), (d3.Improved, d3.LevelAfter, d3.CheckpointRound));

        // O7, round 4: same signature, same 4/5 → EQUAL and repeated → level 2, no rollback.
        var (req4, d4) = Round(policy, C("r4"), V(VerificationState.Assertion, "ASSERTION | AssertionError | totalPriceAddsTax", passed: 4, assertions: 7, failing: "totalPriceAddsTax"));
        Assert.Equal((1, VerificationState.Assertion, 3), (req4.Level, req4.State, req4.BaseRound));
        Assert.Equal((Comparison.Equal, false, true, 2), (d4.Comparison, d4.Rollback, d4.RepeatedSignature, d4.LevelAfter));

        // O1, round 5: level 2 → VALID.
        var (req5, d5) = Round(policy, C("r5"), V(VerificationState.Valid, passed: 5));
        Assert.Equal((2, 3, 5), (req5.Level, req5.BaseRound, req5.Round));
        Assert.True(d5.Improved);
        Assert.Equal(0, policy.Counters.Rollback);
    }

    // Parity: RepairPolicyTests.Q5LevelThreeRotatesBackToTwoWithLongerAttemptList (P04 §12 item 2a)
    [Fact]
    public void Q5LevelThreeRotatesBackToTwoWithLongerAttemptList()
    {
        var policy = Policy(V(VerificationState.Compile, "COMPILE | a | -", failing: "m1"));
        Round(policy, C("r1"), V(VerificationState.Compile, "COMPILE | a | -", failing: "m1")); // repeated → 2
        Round(policy, C("r2"), V(VerificationState.Compile, "COMPILE | a | -", failing: "m1")); // repeated → 3
        var request3 = policy.NextRequest();
        Assert.Equal((3, 3), (request3.RequestedLevel, request3.Level));
        Assert.Equal(["m1"], request3.RewriteMethods);
        Assert.Equal(2, request3.FailedAttempts.Count);

        // Level 3 gives duplicate code → back to 2; the next prompt has one more attempt, so it differs.
        var (_, d3) = Round(policy, C("r2"), null);
        Assert.Equal((3, "repair/user/level3.md", true, 2), (d3.Level, d3.Template, d3.DuplicateCode, d3.LevelAfter));
        var request4 = policy.NextRequest();
        Assert.Equal((2, 3), (request4.Level, request4.FailedAttempts.Count));
        Assert.Empty(request4.RewriteMethods);
        Assert.Equal((1, 3), (policy.Counters.Level3Rounds, policy.Counters.LevelUp));
    }

    // Parity: RepairPolicyTests.Q5LevelThreeNotApplicableUsesLevelTwo (P04 §12 item 2b)
    [Fact]
    public void Q5LevelThreeNotApplicableUsesLevelTwo()
    {
        // Level 3 asked, but the checkpoint is DISCOVERY → that round uses level 2.
        var policy = Policy(V(VerificationState.Discovery, "DISCOVERY | no tests executed | -"));
        Round(policy, C("r1"), V(VerificationState.Discovery, "DISCOVERY | no tests executed | -"));
        Round(policy, C("r2"), V(VerificationState.Discovery, "DISCOVERY | no tests executed | -"));
        var request = policy.NextRequest();
        Assert.Equal((3, 2), (request.RequestedLevel, request.Level));
        Assert.Empty(request.RewriteMethods);

        // COMPILE without a known failing method: also level 2. UNKNOWN and REGRESSION never use level 3.
        Assert.Equal(2, AdaptiveRepairPolicy.EffectiveLevel(3, Version(0, "c0", V(VerificationState.Compile))));
        Assert.Equal(2, AdaptiveRepairPolicy.EffectiveLevel(3, Version(0, "c0", V(VerificationState.Unknown, failing: "m"))));
        Assert.Equal(2, AdaptiveRepairPolicy.EffectiveLevel(3, Version(0, "c0", V(VerificationState.Regression, failing: "m"))));
        Assert.Equal(3, AdaptiveRepairPolicy.EffectiveLevel(3, Version(0, "c0", V(VerificationState.Runtime, failing: "m"))));
        Assert.Equal(1, Policy(V(VerificationState.Compile, "COMPILE | a | -")).NextRequest().Level);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void NextLevelGoesUpThenBackFromThreeToTwo(int level, int next) => Assert.Equal(next, AdaptiveRepairPolicy.NextLevel(level));

    // Parity: RepairPolicyTests.InvalidOutputCountsAsRoundAndRaisesLevel
    [Fact]
    public void InvalidOutputCountsAsRoundAndRaisesLevel()
    {
        var policy = Policy(V(VerificationState.Compile));
        var (_, decision) = Round(policy, Invalid, null);
        Assert.Equal((true, 2, 0, 1), (decision.InvalidOutput, decision.LevelAfter, decision.CheckpointRound, decision.NoImprove));
        Assert.Equal(((VerificationState?)null, (string?)null), (decision.State, decision.CodeHash));
        Assert.Equal("invalid output: no Java class found", policy.Attempts[0].Signature);
        Assert.Null(policy.Attempts[0].Change);
        Assert.Equal(1, policy.Counters.InvalidOutput);

        var parse = Invalid with { InvalidReason = InvalidReasons.ParseError, ParseError = "';' expected at line 4" };
        Round(policy, parse, null);
        Assert.Equal(("invalid output: the code does not parse", "';' expected at line 4"), (policy.Attempts[1].Signature, policy.Attempts[1].FirstError));
    }

    // P04 §12 item 4 (R03 §15.2): B1–B5 are rollbacks, N1 and N2 are BETTER, N3 keeps the level, N4 raises it without a
    // rollback, N5 (duplicate or invalid) is not compared and raises the level.
    [Theory]
    [InlineData("B1", VerificationState.Compile, "COMPILE | other | -", 0, 0, Comparison.Worse, true, 2)]
    [InlineData("B2", VerificationState.Assertion, "ASSERTION | AssertionError | b", 2, 9, Comparison.Worse, true, 2)]
    [InlineData("B3", VerificationState.Assertion, "ASSERTION | AssertionError | b", 4, 6, Comparison.Worse, true, 2)]
    [InlineData("B4", VerificationState.Runtime, "RUNTIME | NullPointerException | A.b", 4, 9, Comparison.Worse, true, 2)]
    [InlineData("B5", VerificationState.Discovery, "DISCOVERY | no tests executed | -", 0, 9, Comparison.Worse, true, 2)]
    [InlineData("N1", VerificationState.Regression, "REGRESSION | existing tests fail | CartTest", 5, 9, Comparison.Better, false, 1)]
    [InlineData("N2", VerificationState.Assertion, "ASSERTION | AssertionError | b", 4, 10, Comparison.Better, false, 1)]
    [InlineData("N3", VerificationState.Assertion, "ASSERTION | AssertionError | other", 4, 9, Comparison.Equal, false, 1)]
    [InlineData("N4", VerificationState.Assertion, "ASSERTION | AssertionError | t", 4, 9, Comparison.Equal, false, 2)]
    public void Section15_2Outcomes(string id, VerificationState state, string signature, int passed, int assertions, Comparison comparison, bool rollback, int levelAfter)
    {
        var policy = Policy(V(VerificationState.Assertion, "ASSERTION | AssertionError | t", passed: 4, assertions: 9, failing: "t"));
        var (_, decision) = Round(policy, C(id), V(state, signature, passed, assertions, "t"));
        Assert.Equal((comparison, rollback, levelAfter), (decision.Comparison!.Value, decision.Rollback, decision.LevelAfter));
        Assert.Equal(comparison == Comparison.Better, decision.Improved);
        Assert.Equal(comparison == Comparison.Better ? 1 : 0, policy.Best.Round);
        Assert.Equal(rollback ? 1 : 0, policy.Counters.Rollback);
        Assert.Equal(id == "N4", decision.RepeatedSignature);
    }

    [Fact]
    public void Section15_2N5DuplicateAndInvalidAreNotCompared()
    {
        var policy = Policy(V(VerificationState.Assertion, "ASSERTION | AssertionError | t", passed: 4, failing: "t"));
        var (_, duplicate) = Round(policy, C("c0"), null);
        Assert.Equal((true, 0, (Comparison?)null, false, 2), (duplicate.DuplicateCode, duplicate.DuplicateOfRound!.Value, duplicate.Comparison, duplicate.Rollback, duplicate.LevelAfter));
        var (_, invalid) = Round(policy, Invalid, null);
        Assert.Equal((true, (Comparison?)null, 3), (invalid.InvalidOutput, invalid.Comparison, invalid.LevelAfter));
        Assert.Equal(0, policy.Counters.Rollback);
    }

    // Parity: RepairPolicyTests.Section15_3RegressionThenRollbackThenValid (P04 §12 item 5)
    [Fact]
    public void Section15_3RegressionThenRollbackThenValid()
    {
        var policy = Policy(V(VerificationState.Assertion, "ASSERTION | AssertionError | t", passed: 4, failing: "t"));
        var (_, d1) = Round(policy, C("r1"), V(VerificationState.Regression, "REGRESSION | existing tests fail | InvoiceTest", passed: 5));
        Assert.Equal((true, 1), (d1.Improved, d1.LevelAfter));
        var (_, d2) = Round(policy, C("r2"), V(VerificationState.Assertion, "ASSERTION | AssertionError | t", passed: 4, failing: "t"));
        Assert.Equal((true, 2, 1), (d2.Rollback, d2.LevelAfter, d2.CheckpointRound));
        var request3 = policy.NextRequest();
        Assert.Equal((2, VerificationState.Regression, 1, 3), (request3.Level, request3.State, request3.BaseRound, request3.Round));
        Assert.Empty(request3.RewriteMethods);
        var (_, d3) = Round(policy, C("r3"), V(VerificationState.Valid, passed: 5));
        Assert.Equal((VerificationState.Valid, 3), (d3.State!.Value, policy.Best.Round));
    }

    // P04 §12 item 6: P = 3 rounds in a row without improvement stop the chain; a BETTER round resets the counter.
    [Fact]
    public void StopsAfterPRoundsWithoutImprovement()
    {
        var policy = Policy(V(VerificationState.Compile, "COMPILE | a | -"), p: 3);
        Round(policy, C("r1"), V(VerificationState.Compile, "COMPILE | b | -"));
        Round(policy, C("r2"), V(VerificationState.Compile, "COMPILE | c | -"));
        Assert.True(policy.CanContinue(2, 5));
        Round(policy, C("r3"), V(VerificationState.Runtime, "RUNTIME | X | -")); // better: the counter starts again
        Assert.Equal(0, policy.NoImprove);
        Round(policy, C("r4"), V(VerificationState.Runtime, "RUNTIME | Y | -"));
        Round(policy, Invalid, null);
        Assert.True(policy.CanContinue(5, 10));
        Assert.Null(policy.StopReason);
        var (_, d6) = Round(policy, C("r4"), null); // duplicate
        Assert.Equal(3, d6.NoImprove);
        Assert.False(policy.CanContinue(6, 10));
        Assert.Equal(StopReason.Stagnation, policy.StopReason);
    }

    [Fact]
    public void StopsAtTheBudgetAndStagnationWinsInTheLastRound()
    {
        var off = Policy(V(VerificationState.Compile, "COMPILE | a | -"));
        Round(off, C("r1"), V(VerificationState.Compile, "COMPILE | b | -"));
        Assert.False(off.CanContinue(1, 1));
        Assert.Equal(StopReason.Budget, off.StopReason);

        var p3 = Policy(V(VerificationState.Compile, "COMPILE | a | -"), p: 3);
        Round(p3, C("r1"), V(VerificationState.Compile, "COMPILE | b | -"));
        Round(p3, C("r2"), V(VerificationState.Compile, "COMPILE | c | -"));
        Round(p3, C("r3"), V(VerificationState.Compile, "COMPILE | d | -"));
        Assert.False(p3.CanContinue(3, 3));
        Assert.Equal(StopReason.Stagnation, p3.StopReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void EarlyStopBelowThreeIsRefused(int p) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(V(VerificationState.Compile), p: p));

    // Parity: RepairPolicyTests.AttemptsCarryFirstErrorAndChangedLines
    [Fact]
    public void AttemptsCarryFirstErrorAndChangedLines()
    {
        var policy = Policy(V(VerificationState.Compile, "COMPILE | cannot find symbol | method"), "class T {\n  long total = service.getTotal(1L);\n}");
        Round(policy, C("class T {\n  long total = service.calcTotal(1L);\n}"), V(VerificationState.Compile, "COMPILE | cannot find symbol | method"));
        var attempt = Assert.Single(policy.Attempts);
        Assert.Equal(1, attempt.Round);
        Assert.Equal("first error of COMPILE | cannot find symbol | method", attempt.FirstError);
        Assert.Equal("-  long total = service.getTotal(1L);\n+  long total = service.calcTotal(1L);", attempt.Change);
    }

    [Fact]
    public void DuplicatesAreCheckedAgainstRolledBackVersionsToo()
    {
        var policy = Policy(V(VerificationState.Runtime, "RUNTIME | X | A.b", failing: "t"));
        Round(policy, C("worse"), V(VerificationState.Compile, "COMPILE | a | -")); // rolled back
        Assert.Equal(1, policy.DuplicateOf("sha256:worse"));
        var (_, d2) = Round(policy, C("worse"), null);
        Assert.Equal((true, 1), (d2.DuplicateCode, d2.DuplicateOfRound!.Value));
        Assert.Equal(0, policy.DuplicateOf("sha256:c0"));
        Assert.Null(policy.DuplicateOf("sha256:new"));
    }

    [Fact]
    public void RequestsCarryTheCheckpointAndAttemptsOnlyFromLevelTwo()
    {
        var policy = Policy(V(VerificationState.Compile, "COMPILE | a | -", failing: "m1"));
        var first = policy.NextRequest();
        Assert.Equal((Cut, 1, 1, "c0", 0, "error of COMPILE | a | -"), (first.CutFqn, first.Round, first.Level, first.BaseCode, first.BaseRound, first.ErrorBlock));
        Assert.Null(first.ExtraContext);
        Round(policy, C("r1"), V(VerificationState.Compile, "COMPILE | a | -", failing: "m1"));
        Assert.Empty(first.FailedAttempts);
        var second = policy.NextRequest();
        Assert.Equal((2, 2, 1), (second.Round, second.Level, second.FailedAttempts.Count));
    }

    [Fact]
    public void AnInfraRoundIsNotApplied()
    {
        var policy = Policy(V(VerificationState.Compile));
        Assert.Throws<ArgumentException>(() => policy.Apply(new RoundOutcome(policy.NextRequest(), C("r1"), new VerifyResult { InfraReason = "B3_TIMEOUT" })));
    }

    [Fact]
    public void DecisionJsonUsesTheNamesOfTheSpec()
    {
        var policy = Policy(V(VerificationState.Compile, "COMPILE | a | -"));
        var (_, decision) = Round(policy, C("r1"), V(VerificationState.Compile, "COMPILE | a | -"));
        var json = ArrowJson.Serialize(decision with { LlmTokens = new TokenUsage(6120, 1830) });
        foreach (var name in new[]
                 {
                     "\"round\": 1", "\"base_round\": 0", "\"requested_level\": 1", "\"level\": 1", "\"template\": \"repair/user/level1.md\"",
                     "\"state\": \"COMPILE\"", "\"duplicate_code\": false", "\"duplicate_of_round\": null", "\"invalid_output\": false",
                     "\"comparison\": \"EQUAL\"", "\"rollback\": false", "\"repeated_signature\": true", "\"improved\": false", "\"level_after\": 2",
                     "\"checkpoint_round\": 0", "\"no_improve\": 1", "\"tests_passed\": 0", "\"dropped_methods\": []", "\"extra_context_warnings\": []",
                     "\"llm_tokens\": {", "\"in\": 6120", "\"llm_retries\": 0", "\"replayed\": false",
                 })
        {
            Assert.Contains(name, json, StringComparison.Ordinal);
        }

        Assert.Contains("\"level3_rounds\": 0", ArrowJson.Serialize(policy.Counters), StringComparison.Ordinal);
        Assert.Contains("\"state_change\": 0", ArrowJson.Serialize(policy.Counters), StringComparison.Ordinal);
    }
}
