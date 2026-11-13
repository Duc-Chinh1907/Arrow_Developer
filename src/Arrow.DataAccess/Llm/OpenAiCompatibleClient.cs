using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Arrow.Common.Json;
using Arrow.Common.Llm;
using Arrow.Common.Logging;

namespace Arrow.DataAccess.Llm;

/// <summary>
/// <c>openai-compatible</c> (P03 §3.2): <c>POST {base_url}/chat/completions</c>. Used for OpenAI, OpenRouter, teamorouter, Gemini through
/// its OpenAI endpoint, Ollama, LiteLLM, vLLM and LM Studio. The parameters of the model are sent as they are; none is dropped.
/// </summary>
public sealed class OpenAiCompatibleClient(HttpClient http, LlmEndpoint endpoint, LlmHttpOptions? options = null, TimeProvider? time = null, IAppLog? log = null, Action<LlmRetry>? onRetry = null) : ILlmClient
{
    private readonly LlmHttpSender _sender = new(http, options ?? new LlmHttpOptions(), time ?? TimeProvider.System, log, onRetry);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var url = endpoint.BaseUrl.AbsoluteUri.TrimEnd('/') + "/chat/completions";
        var body = Body(request, endpoint.ModelId);
        var (parsed, attempts, seconds) = await _sender.SendAsync(
            request.CallId,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (endpoint.Key is { } key)
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Reveal());
                }

                return message;
            },
            text => Parse(text, request),
            endpoint.SecretRef,
            ct).ConfigureAwait(false);
        return parsed with { CallId = request.CallId, LatencySeconds = Math.Round(seconds, 3), Attempts = attempts };
    }

    /// <summary>The JSON body: <c>model</c>, <c>messages</c>, the parameters of the model as they are, and <c>stream: false</c>.</summary>
    public static string Body(LlmRequest request, string modelId)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = request.Messages.Select(m => new Dictionary<string, string> { ["role"] = m.Role, ["content"] = m.Content }).ToList(),
        };
        foreach (var (name, value) in request.Params)
        {
            body[name] = value;
        }

        body["stream"] = false;
        return ArrowJson.Serialize(body, indented: false);
    }

    /// <summary>
    /// Reads a 200 body (P03 §3.2). <c>choices</c> missing is not an answer (null, so the send is retried); <c>choices</c> empty or a null
    /// <c>content</c> is an empty answer. Without <c>usage</c> the tokens are estimated as characters / 4.
    /// </summary>
    public static LlmResponse? Parse(string text, LlmRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var content = "";
            string? finish = null;
            if (choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var c))
                {
                    content = ContentText(c);
                }

                finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            }

            var modelId = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            var (promptTokens, completionTokens, estimated) = Usage(root, request, content);
            return new LlmResponse(request.CallId, content, finish, modelId, promptTokens, completionTokens, estimated, 0, 1, false, null, root.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>content</c> as text: a string, or the <c>text</c> of every part of an array.</summary>
    private static string ContentText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Concat(content.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        && (!p.TryGetProperty("type", out var type) || type.GetString() is "text" or "output_text"))
            .Select(p => p.GetProperty("text").GetString())),
        _ => "",
    };

    private static (int Prompt, int Completion, bool Estimated) Usage(JsonElement root, LlmRequest request, string content)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number
            && usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
        {
            return (p.GetInt32(), c.GetInt32(), false);
        }

        return (EstimateTokens(request.Messages.Sum(m => m.Content.Length)), EstimateTokens(content.Length), true);
    }

    /// <summary>Characters / 4, rounded up (P03 §3.2).</summary>
    public static int EstimateTokens(int characters) => (characters + 3) / 4;
}
