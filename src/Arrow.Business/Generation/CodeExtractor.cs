using System.Text.RegularExpressions;

namespace Arrow.Business.Generation;

/// <summary>Code taken from an answer, and what was done to get it.</summary>
/// <param name="Code">The code; null when the answer has no type declaration (<c>no_code</c>).</param>
/// <param name="Normalization">Operations done, in order: <c>fence removed</c>, <c>prose trimmed</c>.</param>
public sealed record ExtractedCode(string? Code, IReadOnlyList<string> Normalization);

/// <summary>
/// Step 5.1 of P02, pure code: the whole answer is the code, unless it has a fence (the first block that declares a type is
/// taken, <c>fence removed</c>); text before the first <c>package</c>, <c>import</c> or type declaration and after the closing brace
/// is cut (<c>prose trimmed</c>). Nothing is parsed here; step 5.2 asks java-helper.
/// </summary>
public static partial class CodeExtractor
{
    /// <summary>Normalization note for a removed fence.</summary>
    public const string FenceRemoved = "fence removed";

    /// <summary>Normalization note for cut text before or after the code.</summary>
    public const string ProseTrimmed = "prose trimmed";

    // The patterns below are from ARROW_Framework/src/Arrow.Service/Block2/CodeExtractor.cs.
    [GeneratedRegex(@"^[ \t]*```[^\n]*\n(?<body>.*?)^[ \t]*```[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*(?:(?:public|protected|private|abstract|final|static|sealed|non-sealed|strictfp)\s+)*(?:class|interface|enum|record|@interface)\s+[\p{L}_$][\p{L}\p{N}_$]*")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^\s*(?:package|import)\s+[\p{L}_$]")]
    private static partial Regex PackageOrImport();

    /// <summary>An annotation alone on a line, such as <c>@RunWith(...)</c> right before the class.</summary>
    [GeneratedRegex(@"^\s*@[\p{L}_$][\p{L}\p{N}_$.]*(\(.*\))?\s*$")]
    private static partial Regex AnnotationLine();

    [GeneratedRegex(@"\bclass\s+[\p{L}_$]|\binterface\s+[\p{L}_$]|\benum\s+[\p{L}_$]|\brecord\s+[\p{L}_$]")]
    private static partial Regex AnyTypeKeyword();

    /// <summary>Takes the code out of <paramref name="response"/>.</summary>
    public static ExtractedCode Extract(string? response)
    {
        var text = (response ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var actions = new List<string>();
        if (text.Contains("```", StringComparison.Ordinal))
        {
            var block = Fence().Matches(text).Select(m => m.Groups["body"].Value).FirstOrDefault(b => AnyTypeKeyword().IsMatch(b));
            if (block is not null)
            {
                text = block;
                actions.Add(FenceRemoved);
            }
            else
            {
                // An opening fence that is never closed (the answer was cut): drop the fence lines and look at the rest.
                var stripped = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal)));
                if (stripped != text && AnyTypeKeyword().IsMatch(stripped))
                {
                    text = stripped;
                    actions.Add(FenceRemoved);
                }
            }
        }

        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => PackageOrImport().IsMatch(l));
        var firstType = Array.FindIndex(lines, l => TypeDeclaration().IsMatch(l));
        if (firstType < 0)
        {
            return new ExtractedCode(null, actions);
        }

        if (start < 0 || start > firstType)
        {
            // No package or import before the type: start at the type, with the annotation lines right above it.
            start = firstType;
            while (start > 0 && AnnotationLine().IsMatch(lines[start - 1]))
            {
                start--;
            }
        }

        var end = Array.FindLastIndex(lines, l => l.Contains('}', StringComparison.Ordinal));
        if (end < firstType)
        {
            // No closing brace after the declaration (cut code): keep everything, so that the parser reports the error (step 5.2).
            end = lines.Length - 1;
        }

        var before = string.Join('\n', lines.Take(start)).Trim();
        var lastLine = lines[end];
        var closing = lastLine.LastIndexOf('}');
        var tailOnLine = closing >= 0 ? lastLine[(closing + 1)..] : "";
        var after = (tailOnLine + "\n" + string.Join('\n', lines.Skip(end + 1))).Trim();
        if (before.Length > 0 || after.Length > 0)
        {
            actions.Add(ProseTrimmed);
        }

        var kept = lines.Skip(start).Take(end - start + 1).ToArray();
        if (closing >= 0 && tailOnLine.Trim().Length > 0)
        {
            kept[^1] = lastLine[..(closing + 1)];
        }

        return new ExtractedCode(string.Join('\n', kept).Trim('\n') + "\n", actions);
    }

    /// <summary>Step 5.3, pure part: drops an <c>import</c> line that repeats an earlier one exactly (the first is kept).</summary>
    public static (string Code, IReadOnlyList<string> Actions) RemoveDuplicateImports(string code)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var actions = new List<string>();
        var kept = new List<string>();
        foreach (var line in code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) && !seen.Add(Regex.Replace(trimmed, @"\s+", " ")))
            {
                actions.Add($"import: removed duplicate {trimmed.TrimEnd(';')[7..].Trim()}");
                continue;
            }

            kept.Add(line);
        }

        return (string.Join('\n', kept), actions);
    }

    /// <summary>Drops a <c>package</c> line that repeats an earlier one exactly; java-helper then sets the package (step 5.3).</summary>
    public static (string Code, IReadOnlyList<string> Actions) RemoveDuplicatePackages(string code)
    {
        var actions = new List<string>();
        var kept = new List<string>();
        var seen = false;
        foreach (var line in code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*package\s+[\p{L}_$][\p{L}\p{N}_$.]*\s*;\s*$"))
            {
                if (seen)
                {
                    actions.Add("package: removed duplicate");
                    continue;
                }

                seen = true;
            }

            kept.Add(line);
        }

        return (string.Join('\n', kept), actions);
    }
}
