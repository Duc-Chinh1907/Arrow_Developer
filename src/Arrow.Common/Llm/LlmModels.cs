using System.Text.Json;

namespace Arrow.Common.Llm;

/// <summary>One message of a request: <c>system</c> or <c>user</c> (P03 §3.1).</summary>
public sealed record LlmMessage(string Role, string Content);

/// <summary>A single question and answer without streaming (P03 §1, §3.1).</summary>
/// <param name="CallId"><c>gen/&lt;cut_fqn&gt;</c> or <c>repair/&lt;cut_fqn&gt;/round_&lt;k&gt;</c> (P03 §5); the key of the replay cache.</param>
/// <param name="Model">Short name of the model in the configuration, e.g. <c>gpt-x</c>.</param>
/// <param name="Messages">System and user messages.</param>
/// <param name="Params">Request parameters sent as they are: <c>temperature</c>, <c>max_tokens</c>, …</param>
public sealed record LlmRequest(string CallId, string Model, IReadOnlyList<LlmMessage> Messages, IReadOnlyDictionary<string, JsonElement> Params);

/// <summary>The answer of the provider (P03 §3.1). It is also what the replay cache stores.</summary>
/// <param name="CallId">The call this answers.</param>
/// <param name="Content">Text of the answer; empty when the provider returned none.</param>
/// <param name="FinishReason"><c>stop</c>, <c>length</c> or another value of the provider; null when absent.</param>
/// <param name="ModelId">Model id the provider answered with, checked against <c>expected_model_id_prefix</c>.</param>
/// <param name="PromptTokens">Input tokens.</param>
/// <param name="CompletionTokens">Output tokens.</param>
/// <param name="UsageEstimated">The provider sent no usage, so the tokens are characters / 4 (P03 §3.2).</param>
/// <param name="LatencySeconds">Time of the successful send.</param>
/// <param name="Attempts">Number of sends; 1 means no retry.</param>
/// <param name="Replayed">True when the answer comes from the cache (P03 §5).</param>
/// <param name="CostUsd">Cost from the price of the model; null when the model has no price. Set by the gateway.</param>
/// <param name="RawBody">The body of the provider as received, for <c>response.json</c>.</param>
public sealed record LlmResponse(
    string CallId,
    string Content,
    string? FinishReason,
    string ModelId,
    int PromptTokens,
    int CompletionTokens,
    bool UsageEstimated,
    double LatencySeconds,
    int Attempts,
    bool Replayed,
    double? CostUsd,
    JsonElement RawBody)
{
    /// <summary>Tokens counted against the budget of the run.</summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>The only way the product calls a model (P03 §12). Failures raise <c>ArrowException</c> with a code of P03 §11.</summary>
public interface ILlmClient
{
    /// <summary>Sends the request and waits for the answer.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
