using System.Text.Json.Serialization;
using Arrow.Common.Json;

namespace Arrow.Common.Config;

/// <summary>The five configuration layers of P09 §3.1, from lowest to highest priority.</summary>
public enum ConfigOrigin
{
    [JsonStringEnumMemberName("default")]
    Default,

    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("project")]
    Project,

    [JsonStringEnumMemberName("env")]
    Environment,

    [JsonStringEnumMemberName("cli")]
    CommandLine,
}

/// <summary>Where <c>arrow config set/unset</c> writes: <c>config.json</c> (user) or <c>.arrow.json</c> (<c>--project</c>).</summary>
public enum ConfigScope
{
    User,
    Project,
}

/// <summary>One leaf of the merged configuration, for <c>arrow config list --origin</c>.</summary>
/// <param name="Key">Dotted key, e.g. <c>repair.rounds</c>.</param>
/// <param name="ValueJson">The value as JSON text, e.g. <c>5</c>, <c>"vi"</c>, <c>null</c>, <c>["jacoco"]</c>.</param>
/// <param name="Origin">Layer the value comes from.</param>
public sealed record ConfigEntry(string Key, string ValueJson, ConfigOrigin Origin);

/// <summary>Merged configuration with the origin of every key (P09 §3.3).</summary>
/// <param name="Config">Typed configuration.</param>
/// <param name="Entries">Every leaf key, sorted by key.</param>
/// <param name="UserConfigPath">Path of <c>ARROW_HOME/config.json</c>.</param>
/// <param name="ProjectConfigPath">Path of <c>&lt;project&gt;/.arrow.json</c>; null when no project root was given.</param>
public sealed record EffectiveConfig(
    ArrowConfig Config,
    IReadOnlyList<ConfigEntry> Entries,
    string UserConfigPath,
    string? ProjectConfigPath)
{
    /// <summary>Origin of a leaf key; null when the key does not exist.</summary>
    public ConfigOrigin? OriginOf(string key) => Entries.FirstOrDefault(e => e.Key == key)?.Origin;
}

/// <summary>Built-in defaults: layer 1 of P09 §3.1, embedded as <c>Config/defaults.json</c>.</summary>
public static class ArrowDefaults
{
    /// <summary>Current <c>schema_version</c> of the configuration files.</summary>
    public const int SchemaVersion = 1;

    private static readonly Lazy<string> Text = new(() =>
    {
        using var stream = typeof(ArrowDefaults).Assembly.GetManifestResourceStream("Arrow.Common.Config.defaults.json")
            ?? throw new InvalidOperationException("Embedded resource Arrow.Common.Config.defaults.json not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The defaults as JSON text.</summary>
    public static string Json => Text.Value;

    /// <summary>The defaults as a typed configuration.</summary>
    public static ArrowConfig Config => ArrowJson.Deserialize<ArrowConfig>(Json);

    /// <summary>
    /// Keys whose children are names chosen by the user (<c>llm.models.gpt-x</c>, <c>java.jdk_homes.17</c>).
    /// Any key below them is allowed; other keys must exist in the defaults.
    /// </summary>
    public static IReadOnlyList<string> OpenMaps { get; } = ["llm.models", "llm.providers", "java.jdk_homes"];

    /// <summary>Keys holding file system paths; a relative value is resolved from the folder of the file that sets it (P09 §3.1).</summary>
    public static bool IsPathKey(string key) =>
        key is "java.maven_path" or "java.gradle_path" or "java.git_path" or "prompts.override_dir"
        || key.StartsWith("java.jdk_homes.", StringComparison.Ordinal);
}
