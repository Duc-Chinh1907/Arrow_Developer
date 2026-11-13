using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Json;
using Arrow.Common.Verification;

namespace Arrow.Business.Tests.Repair;

/// <summary>Checkpoint comparison (P04 §5), the line diff of attempts (P04 §6.3) and <c>verify.json</c> (P04 §3.8).</summary>
public class CheckpointComparerTests
{
    private static VerifyResult V(VerificationState state, int passed, int assertions) =>
        new() { State = state, TestsPassed = passed, AssertionCount = assertions };

    // Parity: RepairPolicyTests.CheckpointComparisonFollowsStateThenPassedThenAssertions (R03 §15.2: B1–B5 worse, N1–N4 not).
    [Theory]
    [InlineData(VerificationState.Assertion, 4, 9, VerificationState.Compile, 0, 0, Comparison.Worse)]
    [InlineData(VerificationState.Assertion, 4, 9, VerificationState.Assertion, 2, 9, Comparison.Worse)]
    [InlineData(VerificationState.Assertion, 4, 9, VerificationState.Assertion, 4, 6, Comparison.Worse)]
    [InlineData(VerificationState.Regression, 5, 9, VerificationState.Assertion, 4, 9, Comparison.Worse)]
    [InlineData(VerificationState.Runtime, 1, 9, VerificationState.Discovery, 0, 9, Comparison.Worse)]
    [InlineData(VerificationState.Assertion, 4, 9, VerificationState.Regression, 5, 9, Comparison.Better)]
    [InlineData(VerificationState.Assertion, 3, 9, VerificationState.Assertion, 4, 9, Comparison.Better)]
    [InlineData(VerificationState.Assertion, 4, 9, VerificationState.Assertion, 4, 10, Comparison.Better)]
    [InlineData(VerificationState.Compile, 0, 3, VerificationState.Unknown, 0, 0, Comparison.Better)]
    [InlineData(VerificationState.Assertion, 4, 9, VerificationState.Assertion, 4, 9, Comparison.Equal)]
    public void FollowsStateThenPassedThenAssertions(
        VerificationState bestState, int bestPassed, int bestAssertions, VerificationState newState, int newPassed, int newAssertions, Comparison expected) =>
        Assert.Equal(expected, CheckpointComparer.Compare(V(newState, newPassed, newAssertions), V(bestState, bestPassed, bestAssertions)));

    [Fact]
    public void InfraCannotBeCompared() =>
        Assert.Throws<ArgumentException>(() => CheckpointComparer.Compare(new VerifyResult { InfraReason = "B3_TIMEOUT" }, V(VerificationState.Compile, 0, 0)));

    // Parity: RepairPolicyTests.LineDiffLimitsToFifteenLines. P04 §6.3: at most 15 lines with the "more" line, one fewer than before.
    [Fact]
    public void LineDiffLimitsToFifteenLines()
    {
        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"a{i}"));
        var after = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"b{i}"));
        var diff = LineDiff.Changes(before, after)!.Split('\n');
        Assert.Equal(15, diff.Length);
        Assert.Equal("… (26 more changed lines)", diff[^1]);
        Assert.Null(LineDiff.Changes("x\ny\n", "x\ny"));
        Assert.Equal("+z", LineDiff.Changes("x\ny", "x\nz\ny"));

        var fifteen = string.Join('\n', Enumerable.Range(1, 15).Select(i => $"c{i}"));
        Assert.Equal(15, LineDiff.Changes("", fifteen)!.Split('\n').Length);
        Assert.DoesNotContain("more changed", LineDiff.Changes("", fifteen), StringComparison.Ordinal);
    }

    // Parity: RepairPolicyTests.AttemptsCarryFirstErrorAndChangedLines (the diff part)
    [Fact]
    public void LineDiffShowsRemovedThenAddedLinesWithoutContext() =>
        Assert.Equal(
            "-  long total = service.getTotal(1L);\n+  long total = service.calcTotal(1L);",
            LineDiff.Changes("class T {\n  long total = service.getTotal(1L);\n}", "class T {\r\n  long total = service.calcTotal(1L);  \r\n\r\n}"));

    [Fact]
    public void VerifyJsonUsesTheNamesOfTheSpec()
    {
        var result = new VerifyResult
        {
            State = VerificationState.Assertion,
            Steps = [new VerifyStepRecord(VerifySteps.Compile, true, 8.1, 1, "logs/compile.log")],
            TestMethods = new TestMethodCounts(5, 4, 1),
            Failures = [new FailureInfo("totalPriceAddsTax", "org.opentest4j.AssertionFailedError", "expected: <110> but was: <100>")],
            CompileErrors = [new CompileError("X.java", 3, 7, "cannot find symbol", "cannot find symbol")],
            Regressions = [new RegressionInfo("a.BTest#c", RegressionKind.GeneratedFailedInModule)],
            ErrorSignature = "ASSERTION | AssertionFailedError | totalPriceAddsTax",
            TestsPassed = 4,
        };
        var json = ArrowJson.Serialize(result);
        Assert.Contains("\"state\": \"ASSERTION\"", json, StringComparison.Ordinal);
        Assert.Contains("\"group\": \"ASSERTION\"", json, StringComparison.Ordinal);
        Assert.Contains("\"infra_reason\": null", json, StringComparison.Ordinal);
        Assert.Contains("\"test_methods\": {", json, StringComparison.Ordinal);
        Assert.Contains("\"col\": 7", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"generated_failed_in_module\"", json, StringComparison.Ordinal);
        Assert.Contains("\"generated_test_ran_in_module\": null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("is_infra", json, StringComparison.Ordinal);

        var back = ArrowJson.Deserialize<VerifyResult>(json);
        Assert.Equal(VerificationState.Assertion, back.State);
        Assert.Equal(result.TestMethods, back.TestMethods);
        Assert.Equal(result.Failures, back.Failures);
        Assert.Equal(result.CompileErrors, back.CompileErrors);

        var infra = ArrowJson.Serialize(new VerifyResult { InfraReason = "B3_TIMEOUT" });
        Assert.Contains("\"state\": \"INFRA\"", infra, StringComparison.Ordinal);
        Assert.Contains("\"group\": null", infra, StringComparison.Ordinal);
        Assert.True(ArrowJson.Deserialize<VerifyResult>(infra).IsInfra);
    }
}
