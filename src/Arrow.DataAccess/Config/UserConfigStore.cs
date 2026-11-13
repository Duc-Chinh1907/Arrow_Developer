using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Json;
using Arrow.DataAccess.IO;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.Config;

/// <summary>Configuration in five layers (P09 §3).</summary>
public interface IConfigStore
{
    /// <summary>
    /// Reads and merges the five layers. Throws <see cref="ArrowException"/> with <c>CFG_PARSE</c>, <c>CFG_INVALID</c>,
    /// <c>CFG_NEWER_SCHEMA</c> or <c>CFG_SECRET_IN_PROJECT</c>. Range checks are done by <c>ConfigValidator</c> in Business.
    /// </summary>
    /// <param name="projectRoot">Project root, to read <c>.arrow.json</c>; null outside a project.</param>
    /// <param name="cliOverrides">Layer 5: dotted key to raw value, e.g. <c>repair.rounds</c> → <c>3</c>.</param>
    EffectiveConfig Load(string? projectRoot, IReadOnlyDictionary<string, string?> cliOverrides);

    /// <summary>
    /// Sets one key in <c>config.json</c> or <c>.arrow.json</c>, keeping the other keys and their order.
    /// <paramref name="check"/> sees the defaults plus the changed file and may throw to refuse the change; the file is
    /// written only when it does not.
    /// </summary>
    void Set(ConfigScope scope, string key, string rawValue, string? projectRoot, Action<ArrowConfig>? check = null);

    /// <summary>Removes one key from <c>config.json</c> or <c>.arrow.json</c>; false when it was not set there.</summary>
    bool Unset(ConfigScope scope, string key, string? projectRoot);
}

/// <summary>File implementation of <see cref="IConfigStore"/>.</summary>
public sealed class UserConfigStore : IConfigStore
{
    /// <summary>Name of the project layer file.</summary>
    public const string ProjectFileName = ".arrow.json";

    private const string EnvironmentPrefix = "ARROW_";
    private const string EnvironmentSeparator = "__";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ArrowHome _home;
    private readonly Func<IEnumerable<KeyValuePair<string, string?>>> _environment;

    /// <param name="home">The data folder holding <c>config.json</c>.</param>
    /// <param name="environment">Environment variables; tests pass their own.</param>
    public UserConfigStore(ArrowHome home, Func<IEnumerable<KeyValuePair<string, string?>>>? environment = null)
    {
        _home = home;
        _environment = environment ?? ReadProcessEnvironment;
    }

    public EffectiveConfig Load(string? projectRoot, IReadOnlyDictionary<string, string?> cliOverrides)
    {
        var merged = Defaults();
        var origins = new Dictionary<string, ConfigOrigin>(StringComparer.Ordinal);

        if (ReadLayer(_home.ConfigFile, _home.Root) is { } user)
        {
            Merge(merged, user, ConfigOrigin.User, origins);
        }

        var projectFile = projectRoot is null ? null : Path.Combine(Path.GetFullPath(projectRoot), ProjectFileName);
        if (projectFile is not null && ReadLayer(projectFile, Path.GetDirectoryName(projectFile)!) is { } project)
        {
            if (FindProviderApiKey(project) is not null)
            {
                throw new ArrowException(ErrorCodes.CfgSecretInProject);
            }

            Merge(merged, project, ConfigOrigin.Project, origins);
        }

        Merge(merged, ParseOverrides(EnvironmentOverrides(), ConfigOrigin.Environment), ConfigOrigin.Environment, origins);
        Merge(merged, ParseOverrides(cliOverrides, ConfigOrigin.CommandLine), ConfigOrigin.CommandLine, origins);

        var config = ToConfig(merged, origins);
        var entries = Flatten(merged)
            .Select(leaf => new ConfigEntry(leaf.Key, leaf.Value?.ToJsonString(ArrowJson.Compact) ?? "null", origins.GetValueOrDefault(leaf.Key, ConfigOrigin.Default)))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
        return new EffectiveConfig(config, entries, _home.ConfigFile, projectFile);
    }

    public void Set(ConfigScope scope, string key, string rawValue, string? projectRoot, Action<ArrowConfig>? check = null)
    {
        var origin = scope == ConfigScope.User ? ConfigOrigin.User : ConfigOrigin.Project;
        CheckKnownKey(key, rawValue, origin);
        if (scope == ConfigScope.Project && IsProviderApiKey(key))
        {
            throw new ArrowException(ErrorCodes.CfgSecretInProject);
        }

        var (file, baseDir) = LayerFile(scope, projectRoot);
        var layer = ReadRaw(file) ?? new JsonObject { ["schema_version"] = ArrowDefaults.SchemaVersion };
        SetAt(layer, key, ParseValue(key, rawValue, origin));

        // Check that the file still gives a valid configuration before writing it.
        var candidate = Defaults();
        Merge(candidate, Prepare(layer.DeepClone().AsObject(), file, baseDir), origin, new Dictionary<string, ConfigOrigin>());
        check?.Invoke(ToConfig(candidate, new Dictionary<string, ConfigOrigin> { [key] = origin }));

        AtomicFile.WriteAllText(file, layer.ToJsonString(ArrowJson.Options) + "\n");
    }

    public bool Unset(ConfigScope scope, string key, string? projectRoot)
    {
        var (file, _) = LayerFile(scope, projectRoot);
        if (ReadRaw(file) is not { } layer || !RemoveAt(layer, key))
        {
            return false;
        }

        AtomicFile.WriteAllText(file, layer.ToJsonString(ArrowJson.Options) + "\n");
        return true;
    }

    private (string File, string BaseDir) LayerFile(ConfigScope scope, string? projectRoot)
    {
        if (scope == ConfigScope.User)
        {
            return (_home.ConfigFile, _home.Root);
        }

        if (projectRoot is null)
        {
            throw new ArgumentException("A project root is needed for the project layer", nameof(projectRoot));
        }

        var root = Path.GetFullPath(projectRoot);
        return (Path.Combine(root, ProjectFileName), root);
    }

    private static JsonObject Defaults() => JsonNode.Parse(ArrowDefaults.Json)!.AsObject();

    /// <summary>Reads a layer file: null when it does not exist; relative paths resolved from <paramref name="baseDir"/>.</summary>
    private static JsonObject? ReadLayer(string file, string baseDir) =>
        ReadRaw(file) is { } layer ? Prepare(layer, file, baseDir) : null;

    /// <summary>Checks <c>schema_version</c> and unknown keys, drops <c>schema_version</c> and resolves relative paths.</summary>
    private static JsonObject Prepare(JsonObject layer, string file, string baseDir)
    {
        if (layer["schema_version"] is JsonValue version && version.TryGetValue<int>(out var number) && number > ArrowDefaults.SchemaVersion)
        {
            throw new ArrowException(ErrorCodes.CfgNewerSchema, args: new Dictionary<string, string> { ["file"] = file });
        }

        // Older schemas would be upgraded here, in memory only (P09 §3.3). Version 1 is the first.
        layer.Remove("schema_version");
        var origin = Path.GetFileName(file) == ProjectFileName ? ConfigOrigin.Project : ConfigOrigin.User;
        foreach (var (key, value) in Flatten(layer).ToList())
        {
            CheckKnownKey(key, value?.ToJsonString() ?? "null", origin);
            if (ArrowDefaults.IsPathKey(key) && value is JsonValue v && v.TryGetValue<string>(out var path) && path.Length > 0 && !Path.IsPathRooted(path))
            {
                SetAt(layer, key, JsonValue.Create(Path.GetFullPath(Path.Combine(baseDir, path))));
            }
        }

        return layer;
    }

    /// <summary>Parses a config file; null when it does not exist; <c>CFG_PARSE</c> with line and column when it is broken.</summary>
    private static JsonObject? ReadRaw(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: DocumentOptions) as JsonObject
                ?? throw new JsonException("The file must contain a JSON object", null, 0, 0);
        }
        catch (JsonException ex)
        {
            throw new ArrowException(ErrorCodes.CfgParse, args: new Dictionary<string, string>
            {
                ["file"] = file,
                ["line"] = ((ex.LineNumber ?? 0) + 1).ToString(CultureInfo.InvariantCulture),
                ["column"] = ((ex.BytePositionInLine ?? 0) + 1).ToString(CultureInfo.InvariantCulture),
            }, inner: ex);
        }
    }

    private IEnumerable<KeyValuePair<string, string?>> EnvironmentOverrides() =>
        _environment()
            .Where(e => e.Key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)
                        && e.Key.Contains(EnvironmentSeparator, StringComparison.Ordinal)
                        && !e.Key.StartsWith("ARROW_SECRET_", StringComparison.OrdinalIgnoreCase))
            .Select(e => new KeyValuePair<string, string?>(
                e.Key[EnvironmentPrefix.Length..].ToLowerInvariant().Replace(EnvironmentSeparator, ".", StringComparison.Ordinal),
                e.Value));

    private static JsonObject ParseOverrides(IEnumerable<KeyValuePair<string, string?>> overrides, ConfigOrigin origin)
    {
        var layer = new JsonObject();
        foreach (var (key, raw) in overrides)
        {
            CheckKnownKey(key, raw ?? "null", origin);
            SetAt(layer, key, ParseValue(key, raw ?? "null", origin));
        }

        return layer;
    }

    /// <summary>
    /// Parses a raw string (environment, command line, <c>config set</c>) by the type of the key in the defaults:
    /// <c>null</c> always means null; booleans and numbers must parse; lists accept JSON or <c>a,b,c</c>; objects need JSON.
    /// Keys whose default is null, and keys inside open maps, take a JSON literal when the text is one, else the text.
    /// </summary>
    private static JsonNode? ParseValue(string key, string raw, ConfigOrigin origin)
    {
        var text = raw.Trim();
        if (text == "null")
        {
            return null;
        }

        var invalid = new ArrowException(ErrorCodes.CfgInvalid, args: Args(key, raw, origin));
        switch (GetAt(Defaults(), key))
        {
            case JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                return bool.TryParse(text, out var flag) ? JsonValue.Create(flag) : throw invalid;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? JsonNode.Parse(text) : throw invalid;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(raw);
            case JsonArray:
                return text.StartsWith('[')
                    ? TryParseJson(text) as JsonArray ?? throw invalid
                    : new JsonArray(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            case JsonObject:
                return TryParseJson(text) as JsonObject ?? throw invalid;
            default:
                return TryParseJson(text) is { } literal && literal.GetValueKind() != JsonValueKind.String ? literal : JsonValue.Create(raw);
        }
    }

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A key must exist in the defaults, or lie inside an open map such as <c>llm.models</c>.</summary>
    private static void CheckKnownKey(string key, string raw, ConfigOrigin origin)
    {
        var known = key.Length > 0
            && (GetAt(Defaults(), key, out var found) is not null || found
                || ArrowDefaults.OpenMaps.Any(map => key.StartsWith(map + ".", StringComparison.Ordinal) && key.Length > map.Length + 1));
        if (!known)
        {
            throw new ArrowException(ErrorCodes.CfgInvalid, "Khoá cấu hình không tồn tại: " + key, Args(key, raw, origin));
        }
    }

    private static ArrowConfig ToConfig(JsonObject merged, IReadOnlyDictionary<string, ConfigOrigin> origins)
    {
        try
        {
            return merged.Deserialize<ArrowConfig>(ArrowJson.Options) ?? throw new JsonException("empty configuration");
        }
        catch (JsonException ex)
        {
            var key = (ex.Path ?? "$").TrimStart('$').TrimStart('.');
            var value = GetAt(merged, key)?.ToJsonString() ?? "null";
            var origin = origins.Where(o => o.Key == key || o.Key.StartsWith(key + ".", StringComparison.Ordinal)).Select(o => o.Value).DefaultIfEmpty(ConfigOrigin.Default).Max();
            throw new ArrowException(ErrorCodes.CfgInvalid, args: Args(key, value, origin), inner: ex);
        }
    }

    private static Dictionary<string, string> Args(string key, string value, ConfigOrigin origin) => new()
    {
        ["key"] = key,
        ["value"] = value,
        ["origin"] = ArrowJson.Serialize(origin).Trim('"'),
    };

    /// <summary>Copies <paramref name="layer"/> into <paramref name="target"/> key by key; objects merge, everything else replaces.</summary>
    private static void Merge(JsonObject target, JsonObject layer, ConfigOrigin origin, Dictionary<string, ConfigOrigin> origins)
    {
        foreach (var (key, _) in Flatten(layer))
        {
            origins[key] = origin;
        }

        MergeObjects(target, layer);
    }

    private static void MergeObjects(JsonObject target, JsonObject layer)
    {
        foreach (var (name, value) in layer)
        {
            if (value is JsonObject child && target[name] is JsonObject existing)
            {
                MergeObjects(existing, child);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }

    /// <summary>Leaf keys of an object: scalars, nulls, arrays and empty objects.</summary>
    private static IEnumerable<KeyValuePair<string, JsonNode?>> Flatten(JsonObject node, string prefix = "")
    {
        foreach (var (name, value) in node)
        {
            var key = prefix.Length == 0 ? name : prefix + "." + name;
            if (value is JsonObject child && child.Count > 0)
            {
                foreach (var leaf in Flatten(child, key))
                {
                    yield return leaf;
                }
            }
            else if (value is not JsonObject)
            {
                yield return new(key, value);
            }
        }
    }

    private static JsonNode? GetAt(JsonObject root, string key) => GetAt(root, key, out _);

    private static JsonNode? GetAt(JsonObject root, string key, out bool exists)
    {
        exists = false;
        JsonNode? node = root;
        foreach (var part in key.Split('.'))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node))
            {
                return null;
            }
        }

        exists = true;
        return node;
    }

    private static void SetAt(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                child = new JsonObject();
                node[part] = child;
            }

            node = child;
        }

        node[parts[^1]] = value;
    }

    private static bool RemoveAt(JsonObject root, string key)
    {
        var parts = key.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                return false;
            }

            node = child;
        }

        return node.Remove(parts[^1]);
    }

    private static string? FindProviderApiKey(JsonObject layer) =>
        Flatten(layer).Select(leaf => leaf.Key).FirstOrDefault(IsProviderApiKey);

    private static bool IsProviderApiKey(string key) =>
        key.StartsWith("llm.providers.", StringComparison.Ordinal) && key.EndsWith(".api_key", StringComparison.Ordinal);

    private static IEnumerable<KeyValuePair<string, string?>> ReadProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => new KeyValuePair<string, string?>((string)e.Key, e.Value as string));
}
