using System.Globalization;
using System.Text.RegularExpressions;

namespace Arrow.Common.Analysis;

/// <summary>Java version strings as major numbers (P01 §4.3), shared by block 1 and the JDK detection of <c>doctor</c>.</summary>
public static partial class JavaVersions
{
    /// <summary>JDK majors ARROW supports (P01 §1).</summary>
    public static IReadOnlyList<int> Supported { get; } = [8, 11, 17, 21];

    [GeneratedRegex("version\\s+\"([^\"]+)\"")]
    private static partial Regex QuotedVersion();

    /// <summary>
    /// Major of a JDK from the output of <c>java -version</c>: <c>openjdk version "1.8.0_504"</c> → 8,
    /// <c>"17.0.20.1" 2026-08-18 LTS</c> → 17, <c>"21"</c> → 21. Null when no version is found.
    /// </summary>
    public static int? ParseRuntimeMajor(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = QuotedVersion().Match(output);
        return match.Success ? ParseLevel(match.Groups[1].Value) : null;
    }

    /// <summary>Full version of a JDK from <c>java -version</c>, e.g. <c>17.0.12</c>; null when not found.</summary>
    public static string? ParseRuntimeVersion(string? output)
    {
        var match = output is null ? null : QuotedVersion().Match(output);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Major of a declared level: <c>1.7</c> → 7, <c>1.8</c> → 8, <c>11</c> → 11, <c>17.0.2</c> → 17,
    /// <c>JavaVersion.VERSION_1_8</c> → 8, <c>VERSION_17</c> → 17. A value that is not a number (such as <c>${java.version}</c>) gives null.
    /// </summary>
    public static int? ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return null;
        }

        var text = level.Trim();
        var version = text.LastIndexOf("VERSION_", StringComparison.Ordinal);
        if (version >= 0)
        {
            text = text[(version + "VERSION_".Length)..].Replace('_', '.');
        }

        var parts = text.Split('.', '_', '-', '+', ' ');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            return null;
        }

        return first == 1 && parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
            ? second
            : first;
    }
}
