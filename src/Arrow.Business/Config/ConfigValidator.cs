using System.Globalization;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Json;

namespace Arrow.Business.Config;

/// <summary>One value that breaks a rule of P09 §3.2.</summary>
/// <param name="Key">Dotted key.</param>
/// <param name="ValueJson">The value as JSON text.</param>
/// <param name="Rule">What the value must be, in the UI language.</param>
public sealed record ConfigIssue(string Key, string ValueJson, string Rule);

/// <summary>
/// Range and reference checks of P09 §3.3 step 2, on top of the type checks done while reading.
/// A missing API key is not checked here: P07 §2 reports <c>CFG_SECRET_MISSING</c> for the provider actually used.
/// </summary>
public static class ConfigValidator
{
    private static readonly string[] QualityTools = ["jacoco", "pit", "tsdetect"];
    private static readonly string[] Languages = ["vi", "en"];

    /// <summary>Every rule that the configuration breaks; empty when it is valid.</summary>
    public static IReadOnlyList<ConfigIssue> Validate(ArrowConfig config)
    {
        var issues = new List<ConfigIssue>();
        void Check(bool ok, string key, object? value, string rule)
        {
            if (!ok)
            {
                issues.Add(new ConfigIssue(key, ArrowJson.Serialize(value, indented: false), rule));
            }
        }

        var llm = config.Llm;
        Check(llm.MaxConcurrent is >= 1 and <= 8, "llm.max_concurrent", llm.MaxConcurrent, "số nguyên từ 1 đến 8");
        Check(llm.RpmLimit >= 1, "llm.rpm_limit", llm.RpmLimit, "số nguyên ≥ 1");
        Check(llm.RequestTimeoutSeconds >= 1, "llm.request_timeout_seconds", llm.RequestTimeoutSeconds, "số nguyên ≥ 1");
        Check(llm.DefaultModel is null || llm.Models.ContainsKey(llm.DefaultModel), "llm.default_model", llm.DefaultModel, "tên một model trong llm.models");

        foreach (var (name, model) in llm.Models)
        {
            var prefix = "llm.models." + name;
            Check(!string.IsNullOrWhiteSpace(model.Provider) && llm.Providers.ContainsKey(model.Provider), prefix + ".provider", model.Provider, "tên một provider trong llm.providers");
            Check(!string.IsNullOrWhiteSpace(model.ModelId), prefix + ".model_id", model.ModelId, "tên model ở provider, không rỗng");
            if (model.PriceUsdPerMillion is { } price)
            {
                Check(price.Input >= 0 && price.Output >= 0, prefix + ".price_usd_per_million", price, "giá ≥ 0");
            }
        }

        foreach (var (name, provider) in llm.Providers)
        {
            var prefix = "llm.providers." + name;
            Check(Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
                prefix + ".base_url", provider.BaseUrl, "URL http hoặc https");
            Check(provider.SecretRef is null || IsSecretReference(provider.SecretRef), prefix + ".secret_ref", provider.SecretRef, "tên khoá gồm a-z, 0-9 và '-'");
        }

        foreach (var (major, home) in config.Java.JdkHomes)
        {
            Check(int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 8, "java.jdk_homes." + major, home, "khoá là số major ≥ 8");
            Check(!string.IsNullOrWhiteSpace(home), "java.jdk_homes." + major, home, "đường dẫn JDK, không rỗng");
        }

        Check(config.Repair.Rounds is >= 1 and <= 10, "repair.rounds", config.Repair.Rounds, "số nguyên từ 1 đến 10");
        Check(config.Repair.EarlyStopP is null or >= 3, "repair.early_stop_p", config.Repair.EarlyStopP, "null hoặc số nguyên ≥ 3");

        var verification = config.Verification;
        Check(verification.BaselineRuns >= 1, "verification.baseline_runs", verification.BaselineRuns, "số nguyên ≥ 1");
        Check(verification.StepTimeoutSeconds >= 1, "verification.step_timeout_seconds", verification.StepTimeoutSeconds, "số nguyên ≥ 1");
        Check(verification.InfraAttempts >= 1, "verification.infra_attempts", verification.InfraAttempts, "số nguyên ≥ 1");
        Check(verification.InfraWaitSeconds >= 0, "verification.infra_wait_seconds", verification.InfraWaitSeconds, "số nguyên ≥ 0");

        var quality = config.Quality;
        Check(quality.Tools.All(QualityTools.Contains) && quality.Tools.Distinct().Count() == quality.Tools.Count, "quality.tools", quality.Tools, "danh sách con của jacoco, pit, tsdetect, không lặp");
        Check(quality.PitTimeoutSeconds >= 1, "quality.pit_timeout_seconds", quality.PitTimeoutSeconds, "số nguyên ≥ 1");
        Check(quality.TsdetectTimeoutSeconds >= 1, "quality.tsdetect_timeout_seconds", quality.TsdetectTimeoutSeconds, "số nguyên ≥ 1");

        Check(config.Run.Jobs is >= 1 and <= 8, "run.jobs", config.Run.Jobs, "số nguyên từ 1 đến 8");
        Check(config.Run.MaxTokens is null or > 0, "run.max_tokens", config.Run.MaxTokens, "null hoặc số nguyên > 0");
        Check(config.Run.ConfirmCostFromClasses >= 1, "run.confirm_cost_from_classes", config.Run.ConfirmCostFromClasses, "số nguyên ≥ 1");

        Check(Languages.Contains(config.Ui.Language), "ui.language", config.Ui.Language, "vi hoặc en");
        return issues;
    }

    /// <summary>Throws <c>CFG_INVALID</c> for the first broken rule, naming the key, its value and its origin.</summary>
    public static void ThrowIfInvalid(EffectiveConfig effective)
    {
        if (Validate(effective.Config) is [var first, ..])
        {
            var origin = effective.OriginOf(first.Key) ?? effective.Entries
                .Where(e => e.Key.StartsWith(first.Key + ".", StringComparison.Ordinal))
                .Select(e => (ConfigOrigin?)e.Origin)
                .Max() ?? ConfigOrigin.Default;
            throw new ArrowException(ErrorCodes.CfgInvalid, args: new Dictionary<string, string>
            {
                ["key"] = first.Key,
                ["value"] = first.ValueJson + " (cần: " + first.Rule + ")",
                ["origin"] = ArrowJson.Serialize(origin).Trim('"'),
            });
        }
    }

    private static bool IsSecretReference(string reference) =>
        reference.Length is > 0 and <= 64
        && (char.IsAsciiLetterLower(reference[0]) || char.IsAsciiDigit(reference[0]))
        && reference.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
