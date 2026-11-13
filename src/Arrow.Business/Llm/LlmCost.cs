using Arrow.Common.Llm;

namespace Arrow.Business.Llm;

/// <summary>The size of the generation prompt of one class, known before any call (P07 §4).</summary>
/// <param name="Characters">Length of the CUT file (shortened when over 48,000), the template and about 1,500 for the block M.</param>
public sealed record PromptSizeHint(int Characters);

/// <summary>Tokens and money for a run, before it starts (P03 §7).</summary>
/// <param name="ExpectedTokens">One generation and <c>min(N, 3)</c> repairs per class.</param>
/// <param name="MaxTokens">One generation and N repairs per class.</param>
/// <param name="ExpectedCostUsd">Null when the model has no price.</param>
/// <param name="MaxCostUsd">Null when the model has no price.</param>
public sealed record CostEstimate(long ExpectedTokens, long MaxTokens, double? ExpectedCostUsd, double? MaxCostUsd);

/// <summary>Estimates the cost of a run (P03 §7).</summary>
public interface ILlmCostEstimator
{
    /// <summary>The estimate for <paramref name="targets"/> classes with a repair budget of <paramref name="rounds"/>.</summary>
    CostEstimate Estimate(ResolvedModel model, IReadOnlyList<PromptSizeHint> targets, int rounds);
}

/// <summary><see cref="ILlmCostEstimator"/> with the formulas of P03 §7.</summary>
public sealed class LlmCostEstimator : ILlmCostEstimator
{
    /// <summary>Output tokens counted for one call, at most (P03 §7).</summary>
    public const int CompletionTokensCap = 3000;

    /// <summary>A repair prompt is the generation prompt plus this (P03 §7).</summary>
    public const int RepairExtraTokens = 2000;

    /// <summary>Repairs counted in the expected figure.</summary>
    public const int ExpectedRepairs = 3;

    public CostEstimate Estimate(ResolvedModel model, IReadOnlyList<PromptSizeHint> targets, int rounds)
    {
        long expectedIn = 0, expectedOut = 0, maxIn = 0, maxOut = 0;
        var completion = Math.Min(model.MaxTokens, CompletionTokensCap);
        foreach (var target in targets)
        {
            long generation = (target.Characters + 3) / 4;
            var repair = generation + RepairExtraTokens;
            var expectedRepairs = Math.Min(Math.Max(rounds, 0), ExpectedRepairs);
            expectedIn += generation + (expectedRepairs * repair);
            expectedOut += (1 + expectedRepairs) * completion;
            maxIn += generation + (Math.Max(rounds, 0) * repair);
            maxOut += (1 + Math.Max(rounds, 0)) * (long)completion;
        }

        double? Cost(long input, long output) => model.Price is null ? null : Math.Round((input * model.Price.Input / 1e6) + (output * model.Price.Output / 1e6), 4);
        return new CostEstimate(expectedIn + expectedOut, maxIn + maxOut, Cost(expectedIn, expectedOut), Cost(maxIn, maxOut));
    }
}

/// <summary><c>usage.json</c> of one call (P03 §7).</summary>
/// <param name="CallId">The call.</param>
/// <param name="Model">Short name in the configuration.</param>
/// <param name="ModelId">Model id the provider answered with.</param>
/// <param name="ProviderHost">Host of the provider; never the full URL.</param>
/// <param name="PromptTokens">Input tokens.</param>
/// <param name="CompletionTokens">Output tokens.</param>
/// <param name="UsageEstimated">The tokens were estimated.</param>
/// <param name="FinishReason"><c>stop</c>, <c>length</c> or another value.</param>
/// <param name="LatencySeconds">Time of the real call.</param>
/// <param name="Attempts">Sends needed; 1 means no retry.</param>
/// <param name="CostUsd">Null when the model has no price.</param>
/// <param name="Replayed">True when the answer came from the cache.</param>
public sealed record LlmUsageRecord(
    string CallId,
    string Model,
    string ModelId,
    string ProviderHost,
    int PromptTokens,
    int CompletionTokens,
    bool UsageEstimated,
    string? FinishReason,
    double LatencySeconds,
    int Attempts,
    double? CostUsd,
    bool Replayed)
{
    /// <summary>The usage of an answer from <paramref name="model"/>.</summary>
    public static LlmUsageRecord From(LlmResponse response, ResolvedModel model) => new(
        response.CallId,
        model.Name,
        response.ModelId,
        model.ProviderHost,
        response.PromptTokens,
        response.CompletionTokens,
        response.UsageEstimated,
        response.FinishReason,
        response.LatencySeconds,
        response.Attempts,
        response.CostUsd,
        response.Replayed);
}
