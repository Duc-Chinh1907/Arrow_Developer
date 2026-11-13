using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arrow.Common.Config;

/// <summary>The merged configuration of P09 §3.2, after the five layers are applied.</summary>
public sealed record ArrowConfig(
    int SchemaVersion,
    LlmSettings Llm,
    JavaSettings Java,
    RepairSettings Repair,
    VerificationSettings Verification,
    QualitySettings Quality,
    ApplySettings Apply,
    RunSettings Run,
    UiSettings Ui,
    PromptsSettings Prompts);

/// <summary><c>llm.*</c>: models, providers and call limits (P03).</summary>
public sealed record LlmSettings(
    string? DefaultModel,
    IReadOnlyDictionary<string, ModelSettings> Models,
    IReadOnlyDictionary<string, ProviderSettings> Providers,
    int MaxConcurrent,
    int RpmLimit,
    int RequestTimeoutSeconds);

/// <summary><c>llm.models.&lt;name&gt;</c>.</summary>
/// <param name="Provider">Key in <c>llm.providers</c>.</param>
/// <param name="ModelId">Model name at the provider.</param>
/// <param name="Params">Request parameters sent as they are, e.g. <c>temperature</c>, <c>max_tokens</c>.</param>
/// <param name="PriceUsdPerMillion">Optional price, used for cost estimates.</param>
/// <param name="ExpectedModelIdPrefix">Optional prefix the endpoint must answer with (P03).</param>
public sealed record ModelSettings(
    string Provider,
    string ModelId,
    IReadOnlyDictionary<string, JsonElement>? Params,
    ModelPrice? PriceUsdPerMillion,
    string? ExpectedModelIdPrefix);

/// <summary>Price in USD per million tokens.</summary>
public sealed record ModelPrice(double Input, double Output);

/// <summary><c>llm.providers.&lt;name&gt;</c>.</summary>
/// <param name="Kind">Wire protocol.</param>
/// <param name="BaseUrl">Endpoint, e.g. <c>https://api.teamorouter.com/v1</c>.</param>
/// <param name="SecretRef">Name of the key in the secret store; null when the provider needs no key.</param>
public sealed record ProviderSettings(ProviderKind Kind, string BaseUrl, string? SecretRef);

/// <summary>Wire protocol of a provider.</summary>
public enum ProviderKind
{
    [JsonStringEnumMemberName("openai-compatible")]
    OpenAiCompatible,

    [JsonStringEnumMemberName("anthropic")]
    Anthropic,
}

/// <summary><c>java.*</c>: JDKs by major version and build tool paths; null means "look in PATH".</summary>
public sealed record JavaSettings(
    IReadOnlyDictionary<string, string> JdkHomes,
    string? MavenPath,
    string? GradlePath,
    string? GitPath);

/// <summary><c>repair.*</c> (P04).</summary>
public sealed record RepairSettings(bool Enabled, int Rounds, int? EarlyStopP);

/// <summary><c>verification.*</c> (P01, P04).</summary>
public sealed record VerificationSettings(int BaselineRuns, int StepTimeoutSeconds, int InfraAttempts, int InfraWaitSeconds);

/// <summary><c>quality.*</c> (P05).</summary>
public sealed record QualitySettings(bool Enabled, IReadOnlyList<string> Tools, int PitTimeoutSeconds, int TsdetectTimeoutSeconds);

/// <summary><c>apply.*</c> (P06).</summary>
public sealed record ApplySettings(bool Confirm);

/// <summary><c>run.*</c> (P07).</summary>
public sealed record RunSettings(int Jobs, long? MaxTokens, int ConfirmCostFromClasses, bool KeepWorkspaces);

/// <summary><c>ui.*</c> (P08).</summary>
public sealed record UiSettings(ColorMode Color, string Language);

/// <summary>When the CLI uses colors.</summary>
public enum ColorMode
{
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("always")]
    Always,

    [JsonStringEnumMemberName("never")]
    Never,
}

/// <summary><c>prompts.*</c> (P02).</summary>
public sealed record PromptsSettings(string? OverrideDir);
