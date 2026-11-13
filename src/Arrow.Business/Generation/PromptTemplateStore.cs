using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Arrow.Common.Errors;

namespace Arrow.Business.Generation;

/// <summary>A prompt template after loading (P02 §3.1). The front matter is removed from <see cref="Body"/>.</summary>
/// <param name="Path">Path below <c>Prompts/</c> with <c>/</c>, e.g. <c>repair/user/level1.md</c>.</param>
/// <param name="Id">Id from the front matter.</param>
/// <param name="Version">Version from the front matter.</param>
/// <param name="Role"><c>system</c>, <c>user</c> or <c>data</c>.</param>
/// <param name="Placeholders">Names declared in the front matter, which equal the <c>{{…}}</c> of the body.</param>
/// <param name="Body">The content of the message, with LF line endings.</param>
/// <param name="Sha256">sha256 of the whole file (front matter included) with LF line endings, in hex: the value of <c>prompts_sha256</c>.</param>
/// <param name="Overridden">The file comes from <c>prompts.override_dir</c>.</param>
public sealed record PromptTemplate(string Path, string Id, string Version, string Role, IReadOnlySet<string> Placeholders, string Body, string Sha256, bool Overridden);

/// <summary>The templates of block 2 (P02 §8).</summary>
public interface IPromptTemplateStore
{
    /// <summary>The template at <paramref name="path"/>, e.g. <c>generation/user/project_aware.md</c>.</summary>
    PromptTemplate Get(string path);

    /// <summary>Every template of <see cref="PromptTemplateStore.Required"/>.</summary>
    IReadOnlyList<PromptTemplate> All();

    /// <summary>Paths of the files taken from <c>prompts.override_dir</c>, for the warning <c>[!] Dùng template tuỳ chỉnh</c>.</summary>
    IReadOnlyList<string> OverriddenFiles { get; }
}

/// <summary>
/// Loads the embedded templates and, when <c>prompts.override_dir</c> is set, the files that replace them (P02 §3.1). Every file is
/// checked at load time, before any run starts: the placeholders in the body and in the front matter must be the same set, and the
/// <c>id</c> must be the one this path has. A file that fails, or an override with another set of placeholders, is
/// <c>B2_TEMPLATE_INVALID</c>.
/// </summary>
public sealed partial class PromptTemplateStore : IPromptTemplateStore
{
    /// <summary>The files of block 2 and the <c>id</c> each must have.</summary>
    public static IReadOnlyDictionary<string, string> Required { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["generation/system/zero_shot_project_aware.md"] = "generation.system.zero_shot_project_aware",
        ["generation/user/project_aware.md"] = "generation.user.project_aware",
        ["repair/system.md"] = "repair.system",
        ["repair/user/level1.md"] = "repair.user.level1",
        ["repair/user/level2.md"] = "repair.user.level2",
        ["repair/user/level3.md"] = "repair.user.level3",
        ["repair/failure_summaries.md"] = "repair.failure_summaries",
    };

    private const string ResourcePrefix = "Arrow.Business.Prompts.";

    private readonly Dictionary<string, PromptTemplate> _templates = new(StringComparer.Ordinal);

    /// <param name="overrideDir">Folder with the same layout as <c>Prompts/</c>; null or a missing folder means no override.</param>
    public PromptTemplateStore(string? overrideDir = null)
    {
        foreach (var (path, id) in Required)
        {
            _templates[path] = Parse(path, ReadEmbedded(path), id, overridden: false);
        }

        var overridden = new List<string>();
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            if (!Directory.Exists(overrideDir))
            {
                throw Invalid($"prompts.override_dir không có: {overrideDir}");
            }

            foreach (var (path, id) in Required)
            {
                var file = System.IO.Path.Combine(overrideDir, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(file))
                {
                    continue;
                }

                var custom = Parse(path, File.ReadAllText(file), id, overridden: true);
                var embedded = _templates[path];
                if (!custom.Placeholders.SetEquals(embedded.Placeholders))
                {
                    throw Invalid($"{path} (ghi đè): bộ placeholder phải giống bản nhúng. Bản nhúng: {Names(embedded.Placeholders)}. Bản ghi đè: {Names(custom.Placeholders)}.");
                }

                _templates[path] = custom;
                overridden.Add(path);
            }
        }

        OverriddenFiles = overridden;
    }

    public IReadOnlyList<string> OverriddenFiles { get; }

    public PromptTemplate Get(string path) =>
        _templates.TryGetValue(path, out var template) ? template : throw Invalid($"không có template {path}");

    public IReadOnlyList<PromptTemplate> All() => Required.Keys.Select(p => _templates[p]).ToList();

    /// <summary>
    /// Reads one template: front matter between two <c>---</c> lines (<c>id</c>, <c>version</c>, <c>role</c>, <c>used_by</c>,
    /// <c>placeholders</c>), then the body. The front matter is required.
    /// </summary>
    public static PromptTemplate Parse(string path, string fileText, string expectedId, bool overridden)
    {
        var text = fileText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw Invalid($"{path}: thiếu phần đầu file (--- … ---).");
        }

        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            throw Invalid($"{path}: phần đầu file không có dòng --- đóng.");
        }

        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                header[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var body = text[(end + 5)..];
        var id = header.GetValueOrDefault("id", "");
        if (id != expectedId)
        {
            throw Invalid($"{path}: id là '{id}', cần '{expectedId}'.");
        }

        var declared = (header.GetValueOrDefault("placeholders") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var used = UsedPlaceholders(body);
        var undeclared = used.Except(declared).Order().ToList();
        var unused = declared.Except(used).Order().ToList();
        if (undeclared.Count > 0 || unused.Count > 0)
        {
            throw Invalid($"{path}: placeholder không khớp. Có trong nội dung mà không khai báo: {Names(undeclared)}. Khai báo mà không có trong nội dung: {Names(unused)}.");
        }

        return new PromptTemplate(path, id, header.GetValueOrDefault("version", ""), header.GetValueOrDefault("role", ""), declared, body, sha, overridden);
    }

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Names of the <c>{{…}}</c> in a body.</summary>
    public static IReadOnlySet<string> UsedPlaceholders(string body) =>
        Placeholder().Matches(body).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Fills the placeholders in one pass, so a value that contains <c>{{x}}</c> (source code can) is not filled again. A value
    /// missing for a placeholder, or a value for a name the template does not have, is <c>B2_TEMPLATE_INVALID</c>: nothing is sent.
    /// </summary>
    public static string Render(PromptTemplate template, IReadOnlyDictionary<string, string> values)
    {
        var missing = template.Placeholders.Where(p => !values.ContainsKey(p)).Order().ToList();
        if (missing.Count > 0)
        {
            throw Invalid($"{template.Path}: thiếu giá trị cho {Names(missing)}.");
        }

        var extra = values.Keys.Where(k => !template.Placeholders.Contains(k)).Order().ToList();
        if (extra.Count > 0)
        {
            throw Invalid($"{template.Path}: có giá trị cho placeholder mà template không có: {Names(extra)}.");
        }

        return Placeholder().Replace(template.Body, m => values[m.Groups[1].Value]);
    }

    private static string ReadEmbedded(string path)
    {
        var assembly = typeof(PromptTemplateStore).Assembly;
        // The logical name keeps the separators of the platform that built the assembly.
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n =>
                n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n[ResourcePrefix.Length..].Replace('\\', '/') == path)
            ?? throw Invalid($"template nhúng {path} không có trong bản build này.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "(không có)" : string.Join(", ", list);
    }

    private static ArrowException Invalid(string detail) => new(ErrorCodes.B2TemplateInvalid, detail);
}
