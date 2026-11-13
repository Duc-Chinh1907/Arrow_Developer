using Arrow.Business.Config;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.UseCases;

/// <summary>Effective configuration and the rules it breaks, for <c>arrow config list</c>.</summary>
public sealed record ConfigListing(EffectiveConfig Effective, IReadOnlyList<ConfigIssue> Issues);

/// <summary>Paths of the two editable layers, for <c>arrow config path</c>.</summary>
public sealed record ConfigPaths(string UserFile, string? ProjectFile);

/// <summary><c>arrow config list|get|set|unset|path</c> (P09 §3.4).</summary>
public sealed class ConfigUseCase(IConfigStore store, ArrowHome home)
{
    private static readonly Dictionary<string, string?> NoOverrides = [];

    /// <summary>The merged configuration with the origin of each key; broken range rules are returned, not thrown.</summary>
    public ConfigListing List(string? projectRoot)
    {
        var effective = store.Load(projectRoot, NoOverrides);
        return new ConfigListing(effective, ConfigValidator.Validate(effective.Config));
    }

    /// <summary>The leaf <paramref name="key"/>, or every leaf below it (e.g. <c>repair</c>).</summary>
    public IReadOnlyList<ConfigEntry> Get(string? projectRoot, string key)
    {
        var entries = store.Load(projectRoot, NoOverrides).Entries
            .Where(e => e.Key == key || e.Key.StartsWith(key + ".", StringComparison.Ordinal))
            .ToList();
        return entries.Count > 0
            ? entries
            : throw new ArrowException(ErrorCodes.CfgInvalid, "Khoá cấu hình không tồn tại: " + key,
                new Dictionary<string, string> { ["key"] = key, ["value"] = "-", ["origin"] = "cli" });
    }

    /// <summary>Writes one key into <c>config.json</c> or <c>.arrow.json</c> after checking its type and range.</summary>
    public void Set(ConfigScope scope, string key, string rawValue, string? projectRoot) =>
        store.Set(scope, key, rawValue, projectRoot, config =>
        {
            // Only the rules about this key: other layers may complete the rest (e.g. a model defined elsewhere).
            var issue = ConfigValidator.Validate(config).FirstOrDefault(i =>
                i.Key == key || i.Key.StartsWith(key + ".", StringComparison.Ordinal) || key.StartsWith(i.Key + ".", StringComparison.Ordinal));
            if (issue is not null)
            {
                throw new ArrowException(ErrorCodes.CfgInvalid, args: new Dictionary<string, string>
                {
                    ["key"] = issue.Key,
                    ["value"] = issue.ValueJson + " (cần: " + issue.Rule + ")",
                    ["origin"] = scope == ConfigScope.User ? "user" : "project",
                });
            }
        });

    /// <summary>Removes one key from a layer; false when it was not set there.</summary>
    public bool Unset(ConfigScope scope, string key, string? projectRoot) => store.Unset(scope, key, projectRoot);

    /// <summary>Paths of <c>config.json</c> and, inside a project, <c>.arrow.json</c>; works even when the files are broken.</summary>
    public ConfigPaths Paths(string? projectRoot) => new(
        home.ConfigFile,
        projectRoot is null ? null : Path.Combine(Path.GetFullPath(projectRoot), UserConfigStore.ProjectFileName));
}
