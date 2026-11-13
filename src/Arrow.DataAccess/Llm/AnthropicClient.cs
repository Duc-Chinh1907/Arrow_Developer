using System.Text;
using System.Text.Json;
using Arrow.Common.Json;
using Arrow.Common.Llm;
using Arrow.Common.Logging;

namespace Arrow.DataAccess.Llm;

/// <summary><c>anthropic</c> (P03 §3.3): the Messages API, <c>POST {base_url}/v1/messages</c>, one user message and a system text.</summary>
public sealed class AnthropicClient(HttpClient http, LlmEndpoint endpoint, LlmHttpOptions? options = null, TimeProvider? time = null, IAppLog? log = null, Action<LlmRetry>? onRetry = null) : ILlmClient
{
    /// <summary>Value of the <c>anthropic-version</c> header.</summary>
    public const string ApiVersion = "2023-06-01";

    private readonly LlmHttpSender _sender = new(http, options ?? new LlmHttpOptions(), time ?? TimeProvider.System, log, onRetry);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var url = endpoint.BaseUrl.AbsoluteUri.TrimEnd('/') + "/v1/messages";
        var body = Body(request, endpoint.ModelId);
        var (parsed, attempts, seconds) = await _sender.SendAsync(
            request.CallId,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (endpoint.Key is { } key)
                {
                    message.Headers.Add("x-api-key", key.Reveal());
                }

                message.Headers.Add("anthropic-version", ApiVersion);
                return message;
            },
            text => Parse(text, request),
            endpoint.SecretRef,
            ct).ConfigureAwait(false);
        return parsed with { CallId = request.CallId, LatencySeconds = Math.Round(seconds, 3), Attempts = attempts };
    }

    /// <summary>
    /// The JSON body: <c>model</c>, <c>system</c> (the system messages joined), the other messages, and the parameters as they are
    /// (<c>max_tokens</c> is required by the API, so it is 16384 when the parameters have none).
    /// </summary>
    public static string Body(LlmRequest request, string modelId)
    {
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        var body = new Dictionary<string, object?>(StringComparer.Ordinal) { ["model"] = modelId };
        if (system.Length > 0)
        {
            body["system"] = system;
        }

        body["messages"] = request.Messages.Where(m => m.Role != "system").Select(m => new Dictionary<string, string> { ["role"] = m.Role, ["content"] = m.Content }).ToList();
        foreach (var (name, value) in request.Params)
        {
            body[name] = value;
        }

        if (!body.ContainsKey("max_tokens"))
        {
            body["max_tokens"] = 16384;
        }

        return ArrowJson.Serialize(body, indented: false);
    }

    /// <summary>Reads a 200 body (P03 §3.3): the <c>text</c> parts joined; <c>end_turn</c> and <c>stop_sequence</c> are <c>stop</c>, <c>max_tokens</c> is <c>length</c>.</summary>
    public static LlmResponse? Parse(string text, LlmRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var answer = string.Concat(content.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var type) && type.GetString() == "text"
                            && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                .Select(p => p.GetProperty("text").GetString()));
            var stop = root.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var finish = stop switch { "end_turn" or "stop_sequence" => "stop", "max_tokens" => "length", _ => stop };
            var modelId = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";

            int promptTokens, completionTokens;
            var estimated = false;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number
                && usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number)
            {
                (promptTokens, completionTokens) = (i.GetInt32(), o.GetInt32());
            }
            else
            {
                (promptTokens, completionTokens, estimated) = (OpenAiCompatibleClient.EstimateTokens(request.Messages.Sum(x => x.Content.Length)), OpenAiCompatibleClient.EstimateTokens(answer.Length), true);
            }

            return new LlmResponse(request.CallId, answer, finish, modelId, promptTokens, completionTokens, estimated, 0, 1, false, null, root.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
