using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arrow.Common;
using Arrow.Common.Events;
using Arrow.Common.Json;
using Arrow.Common.Llm;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Llm;

/// <summary>A file of <c>runs/&lt;run_id&gt;/llm_cache/&lt;key&gt;.json</c> (P03 §5).</summary>
/// <param name="Key">The cache key, equal to the file name.</param>
/// <param name="CallId">The call that made the entry.</param>
/// <param name="StoredAt">When the answer was stored.</param>
/// <param name="Response">The complete answer.</param>
public sealed record LlmCacheEntry(string Key, string CallId, DateTimeOffset StoredAt, LlmResponse Response);

/// <summary>
/// Plays back calls that already completed when a run is resumed (P03 §5). It never replaces a new call: the key holds the call id,
/// the model, the model id, the parameters and the messages, so another round, another model or another prompt is a miss. A replay keeps
/// the latency, tokens, cost and model id of the real call and is not counted against the budget. Only answers that were read
/// successfully are stored.
/// </summary>
public sealed class ReplayingLlmClient(ILlmClient inner, ResolvedModel model, IRunStore runs, RunHandle run, IProgress<ArrowEvent>? progress = null, TimeProvider? time = null) : ILlmClient
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var key = CacheKey(request, model);
        var path = $"llm_cache/{key}.json";
        if (Read(path) is { } entry)
        {
            progress?.Report(new ArrowEvent(_time.GetUtcNow(), ArrowEventTypes.LlmReplayed, null, null, null, EventLevel.Info,
                $"Phát lại {request.CallId} từ cache", new Dictionary<string, object?> { ["call_id"] = request.CallId }));
            return entry.Response with { Replayed = true };
        }

        var response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);
        runs.WriteJson(run, path, new LlmCacheEntry(key, request.CallId, _time.GetUtcNow(), response));
        return response;
    }

    /// <summary>
    /// SHA-256 of the canonical JSON of <c>{call_id, messages, model, model_id, params}</c>: keys sorted by name, no extra spaces,
    /// numbers in their shortest form, so a different order of parameters gives the same key and two rounds with the same prompt do not.
    /// </summary>
    public static string CacheKey(LlmRequest request, ResolvedModel model)
    {
        var text = new StringBuilder();
        text.Append("{\"call_id\":").Append(Quote(request.CallId));
        text.Append(",\"messages\":[");
        text.AppendJoin(',', request.Messages.Select(m => "{\"content\":" + Quote(m.Content) + ",\"role\":" + Quote(m.Role) + "}"));
        text.Append("],\"model\":").Append(Quote(request.Model));
        text.Append(",\"model_id\":").Append(Quote(model.ModelId));
        text.Append(",\"params\":{");
        text.AppendJoin(',', request.Params.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Quote(p.Key) + ":" + Canonical(p.Value)));
        text.Append("}}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private LlmCacheEntry? Read(string path)
    {
        try
        {
            return runs.ReadJson<LlmCacheEntry>(run, path);
        }
        catch (JsonException)
        {
            // A file that was cut by a crash is not an answer: call the model again.
            return null;
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, ArrowJson.Compact);

    /// <summary>One JSON value in canonical form (objects sorted, numbers shortest).</summary>
    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(',', value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => Quote(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(',', value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => Quote(value.GetString()!),
        JsonValueKind.Number => Number(value),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "null",
    };

    /// <summary><c>0</c>, <c>0.0</c> and <c>0e0</c> are all <c>0</c>; <c>0.50</c> is <c>0.5</c>.</summary>
    private static string Number(JsonElement value)
    {
        if (value.TryGetInt64(out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        var number = value.GetDouble();
        return number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
    }
}
