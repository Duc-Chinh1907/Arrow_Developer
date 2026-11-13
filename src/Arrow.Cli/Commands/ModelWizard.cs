using System.Globalization;
using System.Text.RegularExpressions;
using Arrow.Business.UseCases;
using Arrow.Cli.Prompts;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Secrets;

namespace Arrow.Cli.Commands;

/// <summary>
/// Steps 1 and 2 of <c>arrow setup</c> (P08 §2.5), also used by <c>arrow models add</c>: the provider, its URL and key, then the model, its price,
/// a trial call and whether it is the default. Each step says what it does and can go back to the one before.
/// </summary>
internal sealed partial class ModelWizard(CommandContext context)
{
    private readonly IWizardPrompter _ask = context.Get<IWizardPrompter>();
    private readonly IModelsUseCase _models = context.Get<IModelsUseCase>();

    [GeneratedRegex(@"[^A-Za-z0-9_-]+")]
    private static partial Regex Unsafe();

    private sealed record ProviderChoice(ProviderPreset Preset, string Name, string BaseUrl, SecretValue? Key);

    /// <summary>Runs steps 1 and 2. Null when the user went back from the first step, which cancels.</summary>
    public async Task<ModelDraft?> CollectAsync(CancellationToken ct)
    {
        var step = 1;
        ProviderChoice? provider = null;
        ModelDraft? draft = null;
        while (true)
        {
            switch (step)
            {
                case 1:
                    context.Renderer.Line("Bước 1/2. Nhà cung cấp model: chọn nơi ARROW gửi prompt, địa chỉ của nó và khoá API.");
                    provider = AskProvider();
                    if (provider is null)
                    {
                        return null;
                    }

                    step = 2;
                    break;
                case 2:
                    context.Renderer.Line("Bước 2/2. Model: tên model ở nhà cung cấp, tên ngắn để gọi, giá (tuỳ chọn). ARROW gọi thử một lượt.");
                    draft = await AskModelAsync(provider!, ct).ConfigureAwait(false);
                    if (draft is null)
                    {
                        step = 1;
                        break;
                    }

                    return draft;
            }
        }
    }

    private ProviderChoice? AskProvider()
    {
        var presets = ProviderPresets.All;
        var index = _ask.Select("Nhà cung cấp LLM", presets.Select(p => p.Label).ToList(), canGoBack: false);
        var preset = presets[Math.Max(index, 0)];

        var url = _ask.Ask("base_url", preset.BaseUrl.Length > 0 ? preset.BaseUrl : null);
        if (url is null)
        {
            return null;
        }

        while (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            context.Renderer.Warn("base_url phải là URL http hoặc https, ví dụ https://api.teamorouter.com/v1");
            url = _ask.Ask("base_url", preset.BaseUrl.Length > 0 ? preset.BaseUrl : null);
            if (url is null)
            {
                return null;
            }
        }

        var name = preset.Id == "custom" ? _ask.Ask("Tên cho nhà cung cấp này (ví dụ teamo)", Slug(HostLabel(url))) : preset.Name;
        if (name is null)
        {
            return null;
        }

        SecretValue? key = null;
        if (preset.NeedsKey)
        {
            var secretRef = SecretRef(name);
            var existing = context.Get<IModelsUseCase>().List(context.ProjectRoot).Rows.Any(r => r.Provider == name && r.Key != "chưa có khoá");
            var asked = _ask.AskSecret(existing ? $"Khoá API (đã có khoá {secretRef}; Enter để giữ)" : "Khoá API (ký tự được ẩn khi gõ)");
            if (asked.WentBack)
            {
                return null;
            }

            key = asked.Value;
        }

        return new ProviderChoice(preset, name, url, key);
    }

    private async Task<ModelDraft?> AskModelAsync(ProviderChoice provider, CancellationToken ct)
    {
        var modelId = _ask.Ask("model_id (tên model ở nhà cung cấp, ví dụ gpt-4.1-mini)");
        while (modelId is { Length: 0 })
        {
            context.Renderer.Warn("model_id không được để trống.");
            modelId = _ask.Ask("model_id");
        }

        if (modelId is null)
        {
            return null;
        }

        var modelName = _ask.Ask("Tên ngắn của model (dùng với --model)", Slug(modelId));
        if (modelName is null)
        {
            return null;
        }

        var priceText = _ask.Ask("Giá USD / 1 triệu token, dạng \"vào,ra\" (Enter để bỏ qua)");
        if (priceText is null)
        {
            return null;
        }

        var price = ParsePrice(priceText);
        if (priceText.Length > 0 && price is null)
        {
            context.Renderer.Warn("Không đọc được giá, bỏ qua. Dạng đúng: 0.4,1.6");
        }

        var prefix = _ask.Ask("Model id trả về phải bắt đầu bằng (Enter để dùng model_id; - để không kiểm tra)", modelId);
        if (prefix is null)
        {
            return null;
        }

        var draft = new ModelDraft(
            provider.Name,
            provider.Preset.Kind,
            provider.BaseUrl,
            provider.Preset.NeedsKey ? SecretRef(provider.Name) : null,
            provider.Key,
            modelName,
            modelId,
            price,
            prefix is "-" or "" ? null : prefix,
            MakeDefault: true);

        if (_ask.Confirm("Gọi thử một lượt (\"Reply with exactly: OK\", tối đa 16 token)?", true))
        {
            try
            {
                var result = await _models.TestDraftAsync(draft, ct).ConfigureAwait(false);
                Show(result);
            }
            catch (ArrowException ex)
            {
                context.Renderer.Error(context.Catalog.Describe(ex));
                if (!_ask.Confirm("Vẫn lưu model này?", false))
                {
                    return null;
                }
            }
        }

        return draft with { MakeDefault = _ask.Confirm("Đặt làm model mặc định?", true) };
    }

    /// <summary>Prints the result of a trial call as <c>models test</c> does.</summary>
    public void Show(ModelTestResult result)
    {
        var renderer = context.Renderer;
        if (result.PrefixMatches == false)
        {
            renderer.Fail($"model id trả về: {result.ModelId}, không bắt đầu bằng {result.ExpectedPrefix}", "Kiểm tra model_id, hoặc bỏ expected_model_id_prefix.");
        }
        else
        {
            renderer.Ok($"model id trả về: {(result.ModelId.Length == 0 ? "(không có)" : result.ModelId)}{(result.PrefixMatches == true ? " (khớp tiền tố)" : "")}");
        }

        renderer.Line($"Trả lời: {result.Content}");
        renderer.Line($"Token: {result.PromptTokens} vào, {result.CompletionTokens} ra · {result.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s · {(result.CostUsd is { } cost ? cost.ToString("0.########", CultureInfo.InvariantCulture) + " USD" : "chưa có giá")}");
    }

    /// <summary><c>0.4,1.6</c> → input 0.4 and output 1.6; null when it is not two non-negative numbers.</summary>
    internal static ModelPrice? ParsePrice(string text)
    {
        var parts = text.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var input)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var output)
               && input >= 0 && output >= 0
            ? new ModelPrice(input, output)
            : null;
    }

    /// <summary>A short name from a URL: <c>https://api.teamorouter.com/v1</c> gives <c>teamorouter</c>.</summary>
    private static string HostLabel(string url)
    {
        var labels = new Uri(url).Host.Split('.');
        return labels.Length >= 2 ? labels[^2] : labels[0];
    }

    private static string Slug(string text)
    {
        var slug = Unsafe().Replace(text, "-").Trim('-');
        return slug.Length == 0 ? "model" : slug;
    }

    /// <summary>The name of the key of a provider: lower case, a-z, 0-9 and '-'.</summary>
    internal static string SecretRef(string providerName)
    {
        var text = Regex.Replace(providerName.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        return text.Length == 0 ? "key" : text;
    }
}
