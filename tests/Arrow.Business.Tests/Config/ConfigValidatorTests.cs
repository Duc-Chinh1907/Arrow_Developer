using Arrow.Business.Config;
using Arrow.Common.Config;
using Arrow.Common.Errors;

namespace Arrow.Business.Tests.Config;

/// <summary>Range and reference checks of P09 §3.3.</summary>
public class ConfigValidatorTests
{
    private static readonly ArrowConfig Defaults = ArrowDefaults.Config;

    [Fact]
    public void DefaultsAreValid()
    {
        Assert.Empty(ConfigValidator.Validate(Defaults));
    }

    public static TheoryData<string, ArrowConfig> BrokenConfigs => new()
    {
        { "repair.rounds", Defaults with { Repair = Defaults.Repair with { Rounds = 11 } } },
        { "repair.early_stop_p", Defaults with { Repair = Defaults.Repair with { EarlyStopP = 2 } } },
        { "run.jobs", Defaults with { Run = Defaults.Run with { Jobs = 0 } } },
        { "run.max_tokens", Defaults with { Run = Defaults.Run with { MaxTokens = 0 } } },
        { "llm.max_concurrent", Defaults with { Llm = Defaults.Llm with { MaxConcurrent = 9 } } },
        { "llm.default_model", Defaults with { Llm = Defaults.Llm with { DefaultModel = "missing" } } },
        { "quality.tools", Defaults with { Quality = Defaults.Quality with { Tools = ["jacoco", "evosuite"] } } },
        { "ui.language", Defaults with { Ui = Defaults.Ui with { Language = "fr" } } },
        { "verification.baseline_runs", Defaults with { Verification = Defaults.Verification with { BaselineRuns = 0 } } },
        { "java.jdk_homes.7", Defaults with { Java = Defaults.Java with { JdkHomes = new Dictionary<string, string> { ["7"] = "/jdk7" } } } },
    };

    [Theory]
    [MemberData(nameof(BrokenConfigs))]
    public void EachRuleNamesItsKey(string key, ArrowConfig config)
    {
        Assert.Equal(key, Assert.Single(ConfigValidator.Validate(config)).Key);
    }

    [Fact]
    public void EarlyStopMayBeOff()
    {
        Assert.Empty(ConfigValidator.Validate(Defaults with { Repair = Defaults.Repair with { EarlyStopP = null } }));
    }

    [Fact]
    public void ModelsMustPointToProvidersWithHttpUrls()
    {
        var config = Defaults with
        {
            Llm = Defaults.Llm with
            {
                DefaultModel = "gpt-x",
                Models = new Dictionary<string, ModelSettings> { ["gpt-x"] = new("teamo", "gpt-4.1-mini", null, null, null), ["bad"] = new("nope", "", null, null, null) },
                Providers = new Dictionary<string, ProviderSettings> { ["teamo"] = new(ProviderKind.OpenAiCompatible, "ftp://x", "Teamo") },
            },
        };

        var keys = ConfigValidator.Validate(config).Select(i => i.Key).Order().ToList();
        Assert.Equal(["llm.models.bad.model_id", "llm.models.bad.provider", "llm.providers.teamo.base_url", "llm.providers.teamo.secret_ref"], keys);
    }

    [Fact]
    public void ThrowIfInvalidGivesKeyValueAndOrigin()
    {
        var config = Defaults with { Repair = Defaults.Repair with { Rounds = 20 } };
        var effective = new EffectiveConfig(config, [new ConfigEntry("repair.rounds", "20", ConfigOrigin.Environment)], "config.json", null);

        var error = Assert.Throws<ArrowException>(() => ConfigValidator.ThrowIfInvalid(effective));

        Assert.Equal(ErrorCodes.CfgInvalid, error.Code);
        Assert.Equal("repair.rounds", error.Args["key"]);
        Assert.StartsWith("20 (cần: ", error.Args["value"], StringComparison.Ordinal);
        Assert.Equal("env", error.Args["origin"]);
    }
}
