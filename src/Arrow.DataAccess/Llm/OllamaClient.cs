using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arrow.DataAccess.Llm;

/// <summary>What <c>arrow doctor</c> learns from Ollama (P03 §8).</summary>
/// <param name="Running">Ollama answered <c>/api/tags</c>.</param>
/// <param name="HasModel">The model is among the installed ones.</param>
/// <param name="NumCtx">The <c>num_ctx</c> of the model's parameters; null when none is set, so the default of Ollama applies.</param>
/// <param name="Error">Why Ollama could not be asked; null when it answered.</param>
public sealed record OllamaStatus(bool Running, bool HasModel, int? NumCtx, string? Error);

/// <summary>The native API of Ollama, used only by <c>doctor</c> and <c>models test</c> (P03 §8). Calls go through the OpenAI-compatible client.</summary>
public sealed partial class OllamaClient(HttpClient http)
{
    /// <summary>Context window below which Ollama silently cuts the prompt (P03 §8).</summary>
    public const int MinimumContext = 40_960;

    /// <summary>True for <c>localhost:11434</c> and <c>127.0.0.1:11434</c>, the address P03 §8 gives for Ollama.</summary>
    public static bool IsOllama(Uri baseUrl) =>
        baseUrl.Port == 11434 && baseUrl.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    /// <summary>
    /// <c>GET /api/tags</c> to see that Ollama runs and has <paramref name="modelId"/>, then <c>POST /api/show</c> for its <c>num_ctx</c>.
    /// A connection error or a timeout gives <c>Running = false</c> with the reason, never an exception.
    /// </summary>
    public async Task<OllamaStatus> InspectAsync(Uri baseUrl, string modelId, CancellationToken ct)
    {
        var root = new Uri(baseUrl.GetLeftPart(UriPartial.Authority));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var tagsResponse = await http.GetAsync(new Uri(root, "/api/tags"), timeout.Token).ConfigureAwait(false);
            if (!tagsResponse.IsSuccessStatusCode)
            {
                return new OllamaStatus(false, false, null, $"/api/tags trả HTTP {(int)tagsResponse.StatusCode}");
            }

            using var tags = JsonDocument.Parse(await tagsResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var installed = tags.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                ? models.EnumerateArray().Select(m => m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToList()
                : [];
            var has = installed.Any(n => SameModel(n, modelId));
            if (!has)
            {
                return new OllamaStatus(true, false, null, null);
            }

            using var show = await http.PostAsync(
                new Uri(root, "/api/show"),
                new StringContent(JsonSerializer.Serialize(new { model = modelId }), Encoding.UTF8, "application/json"),
                timeout.Token).ConfigureAwait(false);
            if (!show.IsSuccessStatusCode)
            {
                return new OllamaStatus(true, true, null, null);
            }

            using var info = JsonDocument.Parse(await show.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var parameters = info.RootElement.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return new OllamaStatus(true, true, NumCtx(parameters), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new OllamaStatus(false, false, null, ex is OperationCanceledException ? "không trả lời sau 10 s" : ex.Message);
        }
    }

    /// <summary>The model name with or without the <c>:latest</c> tag.</summary>
    internal static bool SameModel(string installed, string wanted) =>
        string.Equals(installed, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(installed, wanted + ":latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(installed + ":latest", wanted, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?m)^\s*num_ctx\s+(\d+)\s*$")]
    private static partial Regex NumCtxLine();

    /// <summary><c>num_ctx</c> from the <c>parameters</c> text of <c>/api/show</c> (lines such as <c>num_ctx 40960</c>); null when absent.</summary>
    public static int? NumCtx(string? parameters) =>
        parameters is not null && NumCtxLine().Match(parameters) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
