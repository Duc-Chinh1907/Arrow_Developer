using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Arrow.Common.Json;

namespace Arrow.Common.Errors;

/// <summary>Message and hint of one error code, as stored in the catalog.</summary>
public sealed record ErrorEntry(string Message, string? Hint);

/// <summary>
/// Explanation and fix hint for every error code (P00 §6), in the UI language (<c>ui.language</c>, P09 §3).
/// The texts live in the embedded resources <c>ErrorCatalog.vi.json</c> and <c>ErrorCatalog.en.json</c>.
/// Texts may contain <c>{name}</c> placeholders, filled from <see cref="ArrowException.Args"/>.
/// </summary>
public sealed partial class ErrorCatalog
{
    /// <summary>Language used when the requested one has no catalog.</summary>
    public const string DefaultLanguage = "vi";

    private static readonly ConcurrentDictionary<string, ErrorCatalog> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ErrorEntry> _entries;
    private readonly IReadOnlyDictionary<string, string> _context;

    private ErrorCatalog(string language, Dictionary<string, ErrorEntry> entries, IReadOnlyDictionary<string, string>? context = null)
    {
        Language = language;
        _entries = entries;
        _context = context ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// The same catalog with <paramref name="name"/> = <paramref name="value"/> for every text that has no value of its own, e.g. the
    /// <c>run_id</c> of a run for the <c>--resume {run_id}</c> hints (GĐ7: these hints were printed with the raw placeholder).
    /// </summary>
    public ErrorCatalog With(string name, string value) =>
        new(Language, _entries, new Dictionary<string, string>(_context) { [name] = value });

    /// <summary>Languages with an embedded catalog.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["vi", "en"];

    /// <summary>Language of this catalog.</summary>
    public string Language { get; }

    /// <summary>Codes present in this catalog.</summary>
    public IReadOnlyCollection<string> Codes => _entries.Keys;

    /// <summary>The catalog of <paramref name="language"/>; falls back to <see cref="DefaultLanguage"/>.</summary>
    public static ErrorCatalog For(string? language)
    {
        var lang = language is not null && Languages.Contains(language, StringComparer.OrdinalIgnoreCase)
            ? language.ToLowerInvariant()
            : DefaultLanguage;
        return Cache.GetOrAdd(lang, Load);
    }

    /// <summary>The raw entry of <paramref name="code"/>, or null when the code is unknown.</summary>
    public ErrorEntry? Find(string code) => _entries.GetValueOrDefault(code);

    /// <summary>
    /// Builds the user-facing error. <paramref name="detail"/>, when given, replaces the catalog message
    /// (it is the specific sentence of this occurrence). Unknown codes keep the code as the message.
    /// Placeholders without a value stay as written, so a missing argument is visible instead of silently empty.
    /// </summary>
    public ErrorInfo Describe(string code, string? detail = null, IReadOnlyDictionary<string, string>? args = null, string? log = null)
    {
        var entry = Find(code);
        if (_context.Count > 0 || log is not null)
        {
            var merged = new Dictionary<string, string>(_context);
            if (log is not null)
            {
                merged["log_path"] = log;
            }

            foreach (var (name, value) in args ?? new Dictionary<string, string>())
            {
                merged[name] = value;
            }

            args = merged;
        }

        var message = detail ?? (entry is null ? code : Fill(entry.Message, args));
        var hint = entry?.Hint is null ? null : Fill(entry.Hint, args);
        return new ErrorInfo(code, message, hint, log);
    }

    /// <summary>Same as <see cref="Describe(string, string?, IReadOnlyDictionary{string, string}?, string?)"/> for an exception.</summary>
    public ErrorInfo Describe(ArrowException exception, string? log = null) =>
        Describe(exception.Code, exception.Detail, exception.Args, log);

    /// <summary>Names of the <c>{name}</c> placeholders in a text.</summary>
    public static IReadOnlySet<string> Placeholders(string? text) =>
        text is null ? new HashSet<string>() : PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    private static string Fill(string text, IReadOnlyDictionary<string, string>? args) =>
        args is null || args.Count == 0
            ? text
            : PlaceholderPattern().Replace(text, m => args.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    private static ErrorCatalog Load(string language)
    {
        var name = $"Arrow.Common.Errors.ErrorCatalog.{language}.json";
        using var stream = typeof(ErrorCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} not found");
        using var reader = new StreamReader(stream);
        var entries = ArrowJson.Deserialize<Dictionary<string, ErrorEntry>>(reader.ReadToEnd());
        return new ErrorCatalog(language, entries);
    }

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex PlaceholderPattern();
}
