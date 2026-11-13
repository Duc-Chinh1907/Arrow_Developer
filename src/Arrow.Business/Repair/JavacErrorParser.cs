using System.Globalization;
using System.Text.RegularExpressions;
using Arrow.Common.Verification;

namespace Arrow.Business.Repair;

/// <summary>
/// Javac errors in a Maven or Gradle log (P04 §3.3). Reads every path style: <c>/Users/a/X.java:[13,6]</c> (macOS),
/// <c>/D:/a/X.java:[13,6]</c> (Maven on Windows), <c>D:\a\X.java:13: error:</c> (Gradle on Windows), and the cut-down
/// format of maven-compiler-plugin 2.x (<c>X.java:[65,44] error: cannot find symbol</c>, then <c>[ERROR]  class Bounds</c>).
/// </summary>
public static partial class JavacErrorParser
{
    // from ARROW_Framework/src/Arrow.Service/Block3/JavacErrorParser.cs (the regex of R03 §3.2)
    [GeneratedRegex(@"^(?:\[ERROR\]\s+)?(?<file>(?:/?[A-Za-z]:)?[^:\[\]\r\n]*?\.java):(?:\[(?<line>\d+),(?<col>\d+)\]|(?<line2>\d+):\s*error:)\s*(?<msg>.*)$")]
    private static partial Regex ErrorLine();

    // from ARROW_Framework/src/Arrow.Service/Block3/JavacErrorParser.cs
    [GeneratedRegex(@"^(?:\[ERROR\])?\s*(?<key>symbol|location)\s*:\s*(?<value>.*)$")]
    private static partial Regex DetailLine();

    /// <summary>The unlabeled location of maven-compiler-plugin 2.x: <c>[ERROR]</c>, white space, then a type (R03 §3.2 step 5).</summary>
    [GeneratedRegex(@"^\[ERROR\]\s+(?<value>(?:class|interface|enum|record)\s+[\p{L}_$][\p{L}\p{N}_$.<>,?\s]*?)\s*$")]
    private static partial Regex BareLocationLine();

    /// <summary>A lint warning (<c>[deprecation] …</c>), which Maven may print as <c>[ERROR]</c> under <c>-Werror</c>.</summary>
    [GeneratedRegex(@"^\[[a-z\-]+\]\s")]
    private static partial Regex LintTag();

    /// <summary>The fixed javac kinds (P04 §4.1), in matching order; "cannot be applied" is decided apart (method or constructor).</summary>
    // from ARROW_Framework/src/Arrow.Service/Block3/JavacErrorParser.cs
    private static readonly (string Kind, string[] Markers)[] Kinds =
    [
        ("cannot find symbol", ["cannot find symbol"]),
        ("incompatible types", ["incompatible types", "bad operand types"]),
        ("unreported exception", ["unreported exception"]),
        ("package does not exist", ["does not exist"]),
        ("has private access", ["has private access"]),
        ("is not public", ["is not public in"]),
        ("cannot be referenced from a static context", ["cannot be referenced from a static context"]),
        ("already defined", ["is already defined"]),
        ("missing return statement", ["missing return statement"]),
        ("unreachable statement", ["unreachable statement"]),
        ("class is abstract", ["is abstract; cannot be instantiated"]),
        ("does not override", ["does not override"]),
        ("cannot be accessed", ["cannot be accessed from outside package", "cannot access"]),
    ];

    /// <summary>Errors of <paramref name="log"/>; Maven prints each error twice, so duplicates are dropped.</summary>
    /// <param name="log">The build log.</param>
    /// <param name="generatedFileNames">File names of the generated suite (e.g. <c>OrderService_ArrowTest.java</c>).</param>
    /// <param name="workspaceRoot">Root of the workspace copy; files under it become relative to the project root.</param>
    public static IReadOnlyList<CompileError> Parse(string log, IReadOnlyCollection<string> generatedFileNames, string? workspaceRoot = null)
    {
        var lines = log.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var errors = new List<CompileError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var match = ErrorLine().Match(lines[i].TrimEnd());
            if (!match.Success || lines[i].TrimStart().StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }

            var message = match.Groups["msg"].Value.Trim();
            if (message.StartsWith("error:", StringComparison.Ordinal))
            {
                message = message["error:".Length..].TrimStart(); // maven-compiler-plugin 2.x
            }

            if (message.Contains("AssertionError", StringComparison.Ordinal)
                || message.StartsWith("warning:", StringComparison.Ordinal)
                || LintTag().IsMatch(message))
            {
                continue;
            }

            var (symbol, location) = Details(lines, i);
            var file = Relative(match.Groups["file"].Value.Trim(), workspaceRoot);
            var line = int.Parse(match.Groups["line"].Success ? match.Groups["line"].Value : match.Groups["line2"].Value, CultureInfo.InvariantCulture);
            int? column = match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value, CultureInfo.InvariantCulture) : null;
            if (!seen.Add($"{FileName(file)}|{line}|{column}|{message}"))
            {
                continue;
            }

            var (symbolKind, symbolName) = SplitSymbol(symbol);
            errors.Add(new CompileError(
                file, line, column, message, Kind(message), symbolKind, symbolName, location,
                generatedFileNames.Contains(FileName(file), StringComparer.Ordinal)));
        }

        return errors;
    }

    /// <summary>The <c>symbol:</c> and <c>location:</c> lines after the error line <paramref name="index"/>, up to the next error.</summary>
    private static (string? Symbol, string? Location) Details(string[] lines, int index)
    {
        string? symbol = null, location = null;
        if (index + 1 < lines.Length && BareLocationLine().Match(lines[index + 1].TrimEnd()) is { Success: true } bare)
        {
            location = bare.Groups["value"].Value.Trim();
        }

        for (var j = index + 1; j < lines.Length && j <= index + 6 && !ErrorLine().IsMatch(lines[j].TrimEnd()); j++)
        {
            var detail = DetailLine().Match(lines[j].Trim());
            if (!detail.Success)
            {
                continue;
            }

            var value = detail.Groups["value"].Value.Trim();
            if (detail.Groups["key"].Value == "symbol")
            {
                symbol ??= value;
            }
            else
            {
                location ??= value;
            }
        }

        return (symbol, location);
    }

    /// <summary>A javac message → one of the fixed kinds of P04 §4.1, or <c>other</c>.</summary>
    public static string Kind(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("cannot be applied to given types", StringComparison.Ordinal) || text.Contains("no suitable", StringComparison.Ordinal))
        {
            return text.StartsWith("constructor", StringComparison.Ordinal) || text.Contains("no suitable constructor", StringComparison.Ordinal)
                ? "constructor cannot be applied"
                : "method cannot be applied";
        }

        return Kinds.FirstOrDefault(k => k.Markers.Any(m => text.Contains(m, StringComparison.Ordinal))).Kind ?? "other";
    }

    /// <summary><c>method getTotal(long)</c> → (<c>method</c>, <c>getTotal(long)</c>).</summary>
    public static (string? Kind, string? Name) SplitSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return (null, null);
        }

        var space = symbol.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (null, symbol) : (symbol[..space], symbol[(space + 1)..].Trim());
    }

    /// <summary>The file name of a path written with <c>/</c> or <c>\</c>.</summary>
    public static string FileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// <paramref name="file"/> relative to <paramref name="root"/>, with <c>/</c>, when it lies under it (also in the
    /// <c>/D:/…</c> form of Maven on Windows); otherwise unchanged.
    /// </summary>
    public static string Relative(string file, string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return file;
        }

        var path = Normalize(file);
        var prefix = Normalize(root).TrimEnd('/') + "/";
        var comparison = OperatingSystem.IsWindows() || HasDrive(path) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(prefix, comparison) ? path[prefix.Length..] : file;
    }

    private static string Normalize(string path)
    {
        var forward = path.Replace('\\', '/');
        return forward.Length > 2 && forward[0] == '/' && char.IsAsciiLetter(forward[1]) && forward[2] == ':' ? forward[1..] : forward;
    }

    private static bool HasDrive(string path) => path.Length > 1 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}
