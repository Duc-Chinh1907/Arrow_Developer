using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Arrow.DataAccess.Security;

/// <summary>
/// Removes API keys from any text before it is written to a log or a result file (P00 §5, P09 §4.5).
/// Every key that ARROW loads is registered here and replaced by <c>***</c>; on top of that, values that look like
/// keys (<c>sk-…</c>, <c>Authorization</c>, <c>x-api-key</c>, <c>api_key</c>, bearer tokens) are masked even when
/// they were never registered.
/// </summary>
public static partial class Redactor
{
    /// <summary>Text that replaces a secret.</summary>
    public const string Mask = "***";

    // Keys shorter than this are not registered: replacing every short string would mangle normal text.
    private const int MinimumRegisteredLength = 6;

    private static readonly ConcurrentDictionary<string, byte> Registered = new(StringComparer.Ordinal);

    /// <summary>Registers a secret so that every later <see cref="Redact"/> removes it.</summary>
    public static void Register(string? secret)
    {
        if (secret is not null && secret.Length >= MinimumRegisteredLength)
        {
            Registered.TryAdd(secret, 0);
        }
    }

    /// <summary>Returns <paramref name="text"/> with every registered secret and every key-like value replaced by <c>***</c>.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Longest first, so that a key containing another registered key is removed whole.
        foreach (var secret in Registered.Keys.OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = AuthorizationHeader().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        text = KeyValue().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        text = Bearer().Replace(text, "Bearer " + Mask);
        text = SkKey().Replace(text, "sk-" + Mask);
        return text;
    }

    // from ARROW_Framework/src/Arrow.DataAccess/Common/Redactor.cs
    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{12,}")]
    private static partial Regex SkKey();

    // from ARROW_Framework/src/Arrow.DataAccess/Common/Redactor.cs
    [GeneratedRegex(@"(?i)\b(api_key|master_key|apikey|x-api-key)(""?\s*[:=]\s*""?)(?!\*\*\*)[^\s"",}]+")]
    private static partial Regex KeyValue();

    // from ARROW_Framework/src/Arrow.DataAccess/Common/Redactor.cs
    [GeneratedRegex(@"(?i)Bearer\s+(?!\*\*\*)[A-Za-z0-9._\-]{8,}")]
    private static partial Regex Bearer();

    // P09 §4.5: the whole value of an Authorization header, whatever the scheme (Bearer, Basic, a raw key).
    [GeneratedRegex(@"(?im)\b(Authorization)(""?\s*[:=]\s*""?)(?!\*\*\*)[^\r\n"",}]+")]
    private static partial Regex AuthorizationHeader();
}
