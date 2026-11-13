using System.Text.Json;
using Arrow.Common.Config;
using Arrow.Common.Errors;

namespace Arrow.Business.Llm;

/// <summary>A model of the configuration joined with its provider, as P03 §2 describes it.</summary>
/// <param name="Name">Short name the user gave, e.g. <c>gpt-x</c>.</param>
/// <param name="ProviderName">Key in <c>llm.providers</c>.</param>
/// <param name="Kind">Wire protocol.</param>
/// <param name="BaseUrl">Endpoint of the provider.</param>
/// <param name="SecretRef">Name of the key in the secret store; null when the provider needs no key.</param>
/// <param name="ModelId">Model name at the provider.</param>
/// <param name="Params">Parameters of every call, the defaults included; the same for the whole run (P03 §2).</param>
/// <param name="Price">USD per million tokens; null when unknown.</param>
/// <param name="ExpectedModelIdPrefix">The answer must come from a model with this prefix; null means no check.</param>
public sealed record ResolvedModel(
    string Name,
    string ProviderName,
    ProviderKind Kind,
    Uri BaseUrl,
    string? SecretRef,
    string ModelId,
    IReadOnlyDictionary<string, JsonElement> Params,
    ModelPrice? Price,
    string? ExpectedModelIdPrefix)
{
    /// <summary>Default <c>temperature</c> (P03 §2).</summary>
    public const int DefaultTemperature = 0;

    /// <summary>Default <c>max_tokens</c> (P03 §2).</summary>
    public const int DefaultMaxTokens = 16384;

    /// <summary>Host name only; the only part of the URL that goes into <c>run.json</c> and <c>usage.json</c> (P09 §5.3).</summary>
    public string ProviderHost => BaseUrl.Host;

    /// <summary><c>max_tokens</c> of the calls.</summary>
    public int MaxTokens => Params.TryGetValue("max_tokens", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : DefaultMaxTokens;

    /// <summary>
    /// The model called <paramref name="name"/> (the default model when null), with <c>temperature = 0</c> and <c>max_tokens = 16384</c>
    /// added when the user set neither. Raises <c>CFG_NO_MODEL</c> when no model is chosen or the name is not configured.
    /// </summary>
    public static ResolvedModel From(ArrowConfig config, string? name)
    {
        var chosen = name ?? config.Llm.DefaultModel ?? throw new ArrowException(ErrorCodes.CfgNoModel);
        if (!config.Llm.Models.TryGetValue(chosen, out var model))
        {
            throw new ArrowException(ErrorCodes.CfgNoModel, $"Không có model tên {chosen} trong cấu hình (llm.models).");
        }

        if (!config.Llm.Providers.TryGetValue(model.Provider, out var provider))
        {
            throw new ArrowException(ErrorCodes.CfgNoModel, $"Model {chosen} dùng provider {model.Provider}, mà llm.providers không có provider đó.");
        }

        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new ArrowException(ErrorCodes.CfgInvalid, args: new Dictionary<string, string>
            {
                ["key"] = $"llm.providers.{model.Provider}.base_url",
                ["value"] = provider.BaseUrl + " (cần: URL http hoặc https)",
                ["origin"] = "user",
            });
        }

        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in model.Params ?? new Dictionary<string, JsonElement>())
        {
            parameters[key] = value;
        }

        if (!parameters.ContainsKey("temperature"))
        {
            parameters["temperature"] = JsonSerializer.SerializeToElement(DefaultTemperature);
        }

        if (!parameters.ContainsKey("max_tokens"))
        {
            parameters["max_tokens"] = JsonSerializer.SerializeToElement(DefaultMaxTokens);
        }

        return new ResolvedModel(chosen, model.Provider, provider.Kind, url, provider.SecretRef, model.ModelId, parameters, model.PriceUsdPerMillion, model.ExpectedModelIdPrefix);
    }
}

/// <summary>The limits of P03 §6 for one run.</summary>
/// <param name="MaxConcurrent"><c>llm.max_concurrent</c>.</param>
/// <param name="RpmLimit"><c>llm.rpm_limit</c>; 0 or less means no limit.</param>
/// <param name="MaxTokens"><c>run.max_tokens</c> or <c>--max-tokens</c>; null means no budget.</param>
/// <param name="RequestTimeout"><c>llm.request_timeout_seconds</c>.</param>
public sealed record LlmLimits(int MaxConcurrent, int RpmLimit, long? MaxTokens, TimeSpan RequestTimeout)
{
    /// <summary>Limits from the configuration; <paramref name="maxTokensOverride"/> is <c>--max-tokens</c>.</summary>
    public static LlmLimits From(ArrowConfig config, long? maxTokensOverride = null) => new(
        config.Llm.MaxConcurrent,
        config.Llm.RpmLimit,
        maxTokensOverride ?? config.Run.MaxTokens,
        TimeSpan.FromSeconds(config.Llm.RequestTimeoutSeconds));
}
