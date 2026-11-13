using System.Text.Json.Serialization;
using Arrow.Common.Verification;

namespace Arrow.Business.Repair;

/// <summary>A new version against the checkpoint (P04 §5).</summary>
public enum Comparison
{
    [JsonStringEnumMemberName("BETTER")]
    Better,

    [JsonStringEnumMemberName("WORSE")]
    Worse,

    [JsonStringEnumMemberName("EQUAL")]
    Equal,
}

/// <summary>
/// P04 §5: the step reached (<c>COMPILE &lt; … &lt; VALID</c>), then <c>tests_passed</c>, then <c>assertion_count</c>.
/// <see cref="Comparison.Equal"/> keeps the checkpoint, which came first.
/// </summary>
public static class CheckpointComparer
{
    /// <summary>How <paramref name="candidate"/> compares with <paramref name="checkpoint"/>; neither may be INFRA.</summary>
    public static Comparison Compare(VerifyResult candidate, VerifyResult checkpoint)
    {
        if (candidate.State is not { } state || checkpoint.State is not { } best)
        {
            throw new ArgumentException("An INFRA result cannot be compared.");
        }

        var byState = state.CompareTo(best);
        if (byState != 0)
        {
            return byState > 0 ? Comparison.Better : Comparison.Worse;
        }

        var byPassed = candidate.TestsPassed.CompareTo(checkpoint.TestsPassed);
        if (byPassed != 0)
        {
            return byPassed > 0 ? Comparison.Better : Comparison.Worse;
        }

        var byAssertions = candidate.AssertionCount.CompareTo(checkpoint.AssertionCount);
        return byAssertions > 0 ? Comparison.Better : byAssertions < 0 ? Comparison.Worse : Comparison.Equal;
    }
}

/// <summary>The changed lines of an attempt against the version it repaired (the <c>change</c> of a failed attempt, P04 §6.3).</summary>
public static class LineDiff
{
    /// <summary>Lines of a diff, the <c>… (k more changed lines)</c> line included.</summary>
    public const int MaxLines = 15;

    /// <summary>
    /// <c>-</c> for a removed line, <c>+</c> for an added one, in file order, without context lines; at most
    /// <paramref name="maxLines"/> lines. Blank lines and trailing white space are ignored. Null when nothing changed.
    /// </summary>
    public static string? Changes(string before, string after, int maxLines = MaxLines)
    {
        var a = Lines(before);
        var b = Lines(after);

        // Common head and tail are skipped; LCS on the middle.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        var x = a[prefix..(a.Length - suffix)];
        var y = b[prefix..(b.Length - suffix)];
        var lcs = new int[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
        {
            for (var j = y.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var changes = new List<string>();
        int p = 0, q = 0;
        while (p < x.Length || q < y.Length)
        {
            if (p < x.Length && q < y.Length && x[p] == y[q])
            {
                p++;
                q++;
            }
            else if (p < x.Length && (q == y.Length || lcs[p + 1, q] >= lcs[p, q + 1]))
            {
                changes.Add("-" + x[p++]);
            }
            else
            {
                changes.Add("+" + y[q++]);
            }
        }

        if (changes.Count == 0)
        {
            return null;
        }

        if (changes.Count <= maxLines)
        {
            return string.Join('\n', changes);
        }

        var kept = maxLines - 1;
        return string.Join('\n', changes.Take(kept)) + $"\n… ({changes.Count - kept} more changed lines)";
    }

    private static string[] Lines(string code) =>
        code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToArray();
}
