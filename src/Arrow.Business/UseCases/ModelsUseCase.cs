using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Arrow.Business.Config;
using Arrow.Business.Llm;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Json;
using Arrow.Common.Llm;
using Arrow.Common.Secrets;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.Llm;
using Arrow.DataAccess.Security;

namespace Arrow.Business.UseCases;

/// <summary>A provider the setup offers (P08 §2.5 step 1, P03 §2).</summary>
/// <param name="Id">Short id.</param>
/// <param name="Label">Text in the menu.</param>
/// <param name="Kind">Wire protocol.</param>
/// <param name="BaseUrl">Suggested <c>base_url</c>; empty when the user types it.</param>
/// <param name="NeedsKey">The provider needs an API key; Ollama does not.</param>
/// <param name="Name">Suggested name of the provider in <c>llm.providers</c>, also the suggested <c>secret_ref</c>.</param>
public sealed record ProviderPreset(string Id, string Label, ProviderKind Kind, string BaseUrl, bool NeedsKey, string Name);

/// <summary>The providers of the setup menu, in the order of P08 §2.5.</summary>
public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", ProviderKind.OpenAiCompatible, "https://api.openai.com/v1", true, "openai"),
        new("custom", "OpenAI-compatible (nhập URL)", ProviderKind.OpenAiCompatible, "", true, "custom"),
        new("anthropic", "Anthropic", ProviderKind.Anthropic, "https://api.anthropic.com", true, "anthropic"),
        new("ollama", "Ollama (trên máy)", ProviderKind.OpenAiCompatible, "http://localhost:11434/v1", false, "ollama"),
        new("openrouter", "OpenRouter", ProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1", true, "openrouter"),
    ];
}

/// <summary>What the user chose in the steps 1 and 2 of <c>arrow setup</c> or <c>arrow models add</c> (P08 §2.5).</summary>
/// <param name="ProviderName">Name in <c>llm.providers</c>.</param>
/// <param name="Kind">Wire protocol.</param>
/// <param name="BaseUrl">Endpoint of the provider.</param>
/// <param name="SecretRef">Name of the key; null for a provider that needs none.</param>
/// <param name="ApiKey">The key to store; null keeps the key that is already stored, or none.</param>
/// <param name="ModelName">Short name of the model, the key in <c>llm.models</c>.</param>
/// <param name="ModelId">Model name at the provider.</param>
/// <param name="Price">USD per million tokens; null when the user gives none.</param>
/// <param name="ExpectedPrefix">Prefix the answering model id must have; null for no check.</param>
/// <param name="MakeDefault">Set <c>llm.default_model</c>.</param>
public sealed record ModelDraft(
    string ProviderName,
    ProviderKind Kind,
    string BaseUrl,
    string? SecretRef,
    SecretValue? ApiKey,
    string ModelName,
    string ModelId,
    ModelPrice? Price,
    string? ExpectedPrefix,
    bool MakeDefault);

/// <summary>A line of the table of <c>arrow models</c> (P03 §10).</summary>
/// <param name="Name">Short name.</param>
/// <param name="Provider">Name of the provider.</param>
/// <param name="Host">Host of the endpoint.</param>
/// <param name="ModelId">Model name at the provider.</param>
/// <param name="Params">Parameters of the calls, the defaults included.</param>
/// <param name="Price">Price text such as <c>0.4 / 1.6</c>; null when unknown.</param>
/// <param name="Key"><c>...xxxx</c>, <c>không cần</c> or <c>chưa có khoá</c>.</param>
/// <param name="IsDefault">The default model.</param>
public sealed record ModelRow(string Name, string Provider, string Host, string ModelId, string Params, string? Price, string Key, bool IsDefault);

/// <summary>The models of the configuration.</summary>
public sealed record ModelsListing(IReadOnlyList<ModelRow> Rows, string? DefaultModel);

/// <summary>Result of <c>arrow models test</c> (P03 §10).</summary>
/// <param name="Model">Short name.</param>
/// <param name="ProviderHost">Host of the endpoint.</param>
/// <param name="Content">What the model answered.</param>
/// <param name="ModelId">Model id the provider answered with.</param>
/// <param name="ExpectedPrefix">The prefix that was expected; null when none is set.</param>
/// <param name="PrefixMatches">The answering model id has the prefix; null when none is set.</param>
/// <param name="PromptTokens">Input tokens.</param>
/// <param name="CompletionTokens">Output tokens.</param>
/// <param name="Seconds">Time of the call.</param>
/// <param name="CostUsd">Null when the model has no price.</param>
public sealed record ModelTestResult(
    string Model,
    string ProviderHost,
    string Content,
    string ModelId,
    string? ExpectedPrefix,
    bool? PrefixMatches,
    int PromptTokens,
    int CompletionTokens,
    double Seconds,
    double? CostUsd);

/// <summary><c>arrow models</c> and the model steps of <c>arrow setup</c> (P03 §10, P08 §2.5).</summary>
public interface IModelsUseCase
{
    /// <summary>The table of <c>arrow models</c>.</summary>
    ModelsListing List(string? projectRoot);

    /// <summary>Sends <c>Reply with exactly: OK</c> with <c>max_tokens = 16</c> to the model (the default one when <paramref name="name"/> is null).</summary>
    Task<ModelTestResult> TestAsync(string? name, string? projectRoot, CancellationToken ct);

    /// <summary>
    /// Tries a model that is not saved yet (step 2 of the setup): the same call as <see cref="TestAsync"/>, with the key of the draft. Nothing is
    /// written. Errors reach the caller as <see cref="ArrowException"/>.
    /// </summary>
    Task<ModelTestResult> TestDraftAsync(ModelDraft draft, CancellationToken ct);

    /// <summary>Stores the key and writes the provider, the model and, when asked, the default model into <c>config.json</c>.</summary>
    void Save(ModelDraft draft, string? projectRoot);

    /// <summary><c>arrow models default &lt;name&gt;</c>.</summary>
    void SetDefault(string name, string? projectRoot);
}

/// <summary>The model lines of <c>arrow doctor</c> (P08 §2.6, P03 §8).</summary>
public interface IModelDiagnostics
{
    /// <summary>The connection to the default model, and for Ollama whether it runs, has the model and a large enough <c>num_ctx</c>.</summary>
    Task<IReadOnlyList<DoctorItem>> CheckAsync(string? projectRoot, CancellationToken ct);
}

/// <summary><see cref="IModelsUseCase"/> and <see cref="IModelDiagnostics"/>.</summary>
public sealed partial class ModelsUseCase(IConfigStore config, ISecretStore secrets, ILlmClientFactory llm, OllamaClient ollama) : IModelsUseCase, IModelDiagnostics
{
    /// <summary>The prompt of <c>models test</c> (P03 §10).</summary>
    public const string TestPrompt = "Reply with exactly: OK";

    /// <summary><c>max_tokens</c> of <c>models test</c>.</summary>
    public const int TestMaxTokens = 16;

    private static readonly Dictionary<string, string?> NoOverrides = [];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex RefPattern();

    public ModelsListing List(string? projectRoot)
    {
        var settings = Load(projectRoot);
        var rows = settings.Llm.Models.OrderBy(m => m.Key, StringComparer.Ordinal).Select(entry =>
        {
            var (name, model) = (entry.Key, entry.Value);
            settings.Llm.Providers.TryGetValue(model.Provider, out var provider);
            var host = Uri.TryCreate(provider?.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "?";
            var parameters = model.Params ?? new Dictionary<string, JsonElement>();
            var shown = new List<string> { "temperature=" + (parameters.TryGetValue("temperature", out var t) ? t.ToString() : ResolvedModel.DefaultTemperature.ToString(CultureInfo.InvariantCulture)) };
            shown.Add("max_tokens=" + (parameters.TryGetValue("max_tokens", out var m) ? m.ToString() : ResolvedModel.DefaultMaxTokens.ToString(CultureInfo.InvariantCulture)));
            shown.AddRange(parameters.Where(p => p.Key is not ("temperature" or "max_tokens")).Select(p => $"{p.Key}={p.Value}"));
            var key = provider?.SecretRef is not { } secretRef ? "không cần" : secrets.Get(secretRef)?.Hint ?? "chưa có khoá";
            var price = model.PriceUsdPerMillion is { } p2 ? $"{p2.Input.ToString(CultureInfo.InvariantCulture)} / {p2.Output.ToString(CultureInfo.InvariantCulture)}" : null;
            return new ModelRow(name, model.Provider, host, model.ModelId, string.Join(", ", shown), price, key, name == settings.Llm.DefaultModel);
        }).ToList();
        return new ModelsListing(rows, settings.Llm.DefaultModel);
    }

    public Task<ModelTestResult> TestAsync(string? name, string? projectRoot, CancellationToken ct)
    {
        var settings = Load(projectRoot);
        return SendTestAsync(ResolvedModel.From(settings, name), LlmLimits.From(settings), null, ct);
    }

    public Task<ModelTestResult> TestDraftAsync(ModelDraft draft, CancellationToken ct)
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            ["temperature"] = JsonSerializer.SerializeToElement(ResolvedModel.DefaultTemperature),
            ["max_tokens"] = JsonSerializer.SerializeToElement(ResolvedModel.DefaultMaxTokens),
        };
        var model = new ResolvedModel(
            draft.ModelName, draft.ProviderName, draft.Kind, new Uri(draft.BaseUrl), draft.SecretRef, draft.ModelId, parameters, draft.Price,
            string.IsNullOrWhiteSpace(draft.ExpectedPrefix) ? null : draft.ExpectedPrefix);
        var limits = new LlmLimits(1, 60, null, TimeSpan.FromSeconds(60));
        return SendTestAsync(model, limits, draft.ApiKey, ct);
    }

    private async Task<ModelTestResult> SendTestAsync(ResolvedModel model, LlmLimits limits, SecretValue? key, CancellationToken ct)
    {
        var small = model with { Params = new Dictionary<string, JsonElement>(model.Params) { ["max_tokens"] = JsonSerializer.SerializeToElement(TestMaxTokens) } };
        var client = llm.CreateProvider(small, limits, retry: false, key: key);

        var response = await client.CompleteAsync(
            new LlmRequest("models-test/" + model.Name, model.Name, [new LlmMessage("user", TestPrompt)], small.Params), ct).ConfigureAwait(false);

        var prefix = model.ExpectedModelIdPrefix is { Length: > 0 } p ? p : null;
        return new ModelTestResult(
            model.Name,
            model.ProviderHost,
            response.Content.Trim(),
            response.ModelId,
            prefix,
            prefix is null ? null : LlmGateway.ModelMatches(prefix, response.ModelId),
            response.PromptTokens,
            response.CompletionTokens,
            response.LatencySeconds,
            LlmGateway.Cost(model.Price, response));
    }

    public void Save(ModelDraft draft, string? projectRoot)
    {
        Require(NamePattern().IsMatch(draft.ProviderName), "tên provider", draft.ProviderName, "chữ, số, '-' và '_', không có dấu chấm");
        Require(NamePattern().IsMatch(draft.ModelName), "tên model", draft.ModelName, "chữ, số, '-' và '_', không có dấu chấm (dấu chấm dùng để ngăn cách các khoá cấu hình)");
        Require(!string.IsNullOrWhiteSpace(draft.ModelId), "model_id", draft.ModelId, "không rỗng");
        Require(draft.SecretRef is null || RefPattern().IsMatch(draft.SecretRef), "tên khoá", draft.SecretRef ?? "", "a-z, 0-9 và '-'");
        Require(Uri.TryCreate(draft.BaseUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https", "base_url", draft.BaseUrl, "URL http hoặc https");

        if (draft is { ApiKey: { } key, SecretRef: { } secretRef })
        {
            secrets.Set(secretRef, key);
        }

        var provider = new JsonObject
        {
            ["kind"] = JsonValue.Create(draft.Kind == ProviderKind.Anthropic ? "anthropic" : "openai-compatible"),
            ["base_url"] = draft.BaseUrl,
            ["secret_ref"] = draft.SecretRef,
        };
        var model = new JsonObject { ["provider"] = draft.ProviderName, ["model_id"] = draft.ModelId };
        if (draft.Price is { } price)
        {
            model["price_usd_per_million"] = new JsonObject { ["input"] = price.Input, ["output"] = price.Output };
        }

        if (!string.IsNullOrWhiteSpace(draft.ExpectedPrefix))
        {
            model["expected_model_id_prefix"] = draft.ExpectedPrefix;
        }

        // Provider first: the check of the model needs it. Each entry is written whole, so the file is valid after every step.
        SetChecked("llm.providers." + draft.ProviderName, provider.ToJsonString(), projectRoot);
        SetChecked("llm.models." + draft.ModelName, model.ToJsonString(), projectRoot);
        if (draft.MakeDefault)
        {
            SetChecked("llm.default_model", draft.ModelName, projectRoot);
        }
    }

    public void SetDefault(string name, string? projectRoot) => SetChecked("llm.default_model", name, projectRoot);

    public async Task<IReadOnlyList<DoctorItem>> CheckAsync(string? projectRoot, CancellationToken ct)
    {
        ArrowConfig settings;
        try
        {
            settings = Load(projectRoot);
        }
        catch (ArrowException)
        {
            // The configuration line of doctor already reports it.
            return [];
        }

        var catalog = ErrorCatalog.For(settings.Ui.Language);
        if (settings.Llm.DefaultModel is null)
        {
            return [new DoctorItem(DoctorLevel.Warning, "Model", "chưa chọn model", "Chạy \"arrow setup\", hoặc \"arrow models add\".")];
        }

        ResolvedModel model;
        try
        {
            model = ResolvedModel.From(settings, null);
        }
        catch (ArrowException ex)
        {
            var info = catalog.Describe(ex);
            return [new DoctorItem(DoctorLevel.Error, "Model", info.Message, info.Hint)];
        }

        var items = new List<DoctorItem>();
        var name = "Model " + model.Name;
        if (OllamaClient.IsOllama(model.BaseUrl))
        {
            var status = await ollama.InspectAsync(model.BaseUrl, model.ModelId, ct).ConfigureAwait(false);
            if (!status.Running)
            {
                items.Add(new DoctorItem(DoctorLevel.Error, "Ollama", $"không chạy ở {model.BaseUrl.Authority}{(status.Error is null ? "" : " (" + status.Error + ")")}", "Khởi động Ollama (ollama serve)."));
                return items;
            }

            if (!status.HasModel)
            {
                items.Add(new DoctorItem(DoctorLevel.Error, "Ollama", $"chưa có model {model.ModelId}", $"ollama pull {model.ModelId}"));
                return items;
            }

            items.Add(status.NumCtx is { } ctx && ctx >= OllamaClient.MinimumContext
                ? new DoctorItem(DoctorLevel.Ok, "Ollama", $"num_ctx {ctx}", null)
                : new DoctorItem(
                    DoctorLevel.Warning,
                    "Ollama",
                    $"num_ctx {(status.NumCtx is { } small ? small.ToString(CultureInfo.InvariantCulture) : "chưa đặt")} nhỏ hơn {OllamaClient.MinimumContext}: Ollama cắt prompt mà không báo lỗi",
                    $"Tạo Modelfile có \"PARAMETER num_ctx {OllamaClient.MinimumContext}\", hoặc đặt OLLAMA_CONTEXT_LENGTH={OllamaClient.MinimumContext} rồi khởi động lại Ollama."));
        }

        try
        {
            var result = await TestAsync(null, projectRoot, ct).ConfigureAwait(false);
            if (result.PrefixMatches == false)
            {
                var info = catalog.Describe(ErrorCodes.B2WrongModel, args: new Dictionary<string, string> { ["actual"] = result.ModelId, ["expected"] = result.ExpectedPrefix! });
                items.Add(new DoctorItem(DoctorLevel.Error, name, info.Message, info.Hint));
            }
            else
            {
                items.Add(new DoctorItem(DoctorLevel.Ok, name, $"{result.ProviderHost} · {result.ModelId} · {result.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s", null));
            }
        }
        catch (ArrowException ex)
        {
            var info = catalog.Describe(ex);
            items.Add(new DoctorItem(DoctorLevel.Error, name, info.Message, info.Hint));
        }

        return items;
    }

    private ArrowConfig Load(string? projectRoot)
    {
        var effective = config.Load(projectRoot, NoOverrides);
        ConfigValidator.ThrowIfInvalid(effective);
        return effective.Config;
    }

    /// <summary>Writes one key of <c>config.json</c>; the rules about that key are checked before the file is written.</summary>
    private void SetChecked(string key, string raw, string? projectRoot) =>
        config.Set(ConfigScope.User, key, raw, projectRoot, merged =>
        {
            var issue = ConfigValidator.Validate(merged).FirstOrDefault(i => i.Key == key || i.Key.StartsWith(key + ".", StringComparison.Ordinal));
            if (issue is not null)
            {
                throw new ArrowException(ErrorCodes.CfgInvalid, args: new Dictionary<string, string>
                {
                    ["key"] = issue.Key,
                    ["value"] = issue.ValueJson + " (cần: " + issue.Rule + ")",
                    ["origin"] = "user",
                });
            }
        });

    private static void Require(bool ok, string what, string value, string rule)
    {
        if (!ok)
        {
            throw new ArrowException(ErrorCodes.CfgInvalid, args: new Dictionary<string, string> { ["key"] = what, ["value"] = value + " (cần: " + rule + ")", ["origin"] = "cli" });
        }
    }
}
