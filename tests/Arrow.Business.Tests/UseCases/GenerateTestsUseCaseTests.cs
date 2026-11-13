using Arrow.Business.Llm;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Errors;

namespace Arrow.Business.Tests.UseCases;

/// <summary>Exit codes (P07 §9) and the estimate line (P07 §4) of <c>arrow gen</c>.</summary>
public class GenerateTestsUseCaseTests
{
    private static TargetResult R(TargetOutcome outcome, ErrorInfo? error = null) => new(
        "a.B", outcome, null, 0, null, null, null, null, null, null, null, null, TokenUsage.Zero, null, 0, new BlockSeconds(null, null, null, null, 0), error, [], null);

    [Fact]
    public void ExitCodesFollowP07()
    {
        Assert.Equal(0, GenerateTestsUseCase.ExitCodeOf([R(TargetOutcome.Valid), R(TargetOutcome.Valid)], false));
        Assert.Equal(1, GenerateTestsUseCase.ExitCodeOf([R(TargetOutcome.Valid), R(TargetOutcome.Assertion)], false));
        Assert.Equal(1, GenerateTestsUseCase.ExitCodeOf([R(TargetOutcome.Blocked), R(TargetOutcome.InvalidOutput)], false));
        Assert.Equal(1, GenerateTestsUseCase.ExitCodeOf([R(TargetOutcome.Valid), R(TargetOutcome.Infra)], false));
        Assert.Equal(2, GenerateTestsUseCase.ExitCodeOf([R(TargetOutcome.Blocked), R(TargetOutcome.Blocked)], false));
        Assert.Equal(2, GenerateTestsUseCase.ExitCodeOf([], false));

        // P03 stopped the run while 2 targets were VALID: still 2 (P07 §8).
        var stopped = R(TargetOutcome.Cancelled, new ErrorInfo(ErrorCodes.B2TokenBudget, "hết ngân sách", null, null));
        Assert.Equal(2, GenerateTestsUseCase.ExitCodeOf([R(TargetOutcome.Valid), R(TargetOutcome.Valid), stopped], true));
    }

    [Fact]
    public void TheEstimateLineFollowsP07()
    {
        Assert.Equal("Ước tính cho 8 class (gpt-x, N=5): ~45k token (~0.03 USD), tối đa ~120k token (~0.08 USD).",
            GenerateTestsUseCase.EstimateText(8, "gpt-x", 5, new CostEstimate(45_000, 120_000, 0.03, 0.08)));
        Assert.Equal("Ước tính cho 1 class (local, N=5): ~900 token, tối đa ~2k token.",
            GenerateTestsUseCase.EstimateText(1, "local", 5, new CostEstimate(900, 2_400, null, null)));
    }
}
