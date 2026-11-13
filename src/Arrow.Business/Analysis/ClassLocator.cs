using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Analysis;

/// <summary>A top-level class the user can pick. <see cref="Path"/> is relative to the project root.</summary>
/// <param name="Fqn">Fully qualified name.</param>
/// <param name="SimpleName">Simple name.</param>
/// <param name="Kind">Kind of type.</param>
/// <param name="IsAbstract">An abstract class.</param>
/// <param name="Path">Source file.</param>
public sealed record ClassCandidate(string Fqn, string SimpleName, CutKind Kind, bool IsAbstract, string Path)
{
    /// <summary>Package; empty for the default package.</summary>
    public string Package => ProjectPaths.SplitFqn(Fqn).Package;
}

/// <summary>Result of matching a CUT query.</summary>
public abstract record LocateResult
{
    private LocateResult()
    {
    }

    /// <summary>Exactly one class; <see cref="Warning"/> is set for abstract classes, enums and records.</summary>
    public sealed record Found(ClassCandidate Class, string? Warning) : LocateResult;

    /// <summary>Several classes match; the CLI lets the user choose (P08 §4.3).</summary>
    public sealed record Ambiguous(IReadOnlyList<ClassCandidate> Candidates) : LocateResult;

    /// <summary>The query cannot be used: <see cref="Code"/> is <c>B1_CUT_MISSING</c> or <c>B1_CUT_NOT_TESTABLE</c>.</summary>
    public sealed record Failed(string Code, string Detail, IReadOnlyList<string> Suggestions) : LocateResult;
}

/// <summary>Finds the CUT among the classes of <c>list_classes</c> (P01 §3.3 steps 3–6).</summary>
public static class ClassLocator
{
    /// <summary>Suggestions given with <c>B1_CUT_MISSING</c>.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>
    /// Matches <paramref name="query"/>: a <c>.java</c> path (relative to the project root, already normalized with <c>/</c>) gives the
    /// top-level class of that file; a name with a dot is an exact FQN; a simple name matches case-sensitively, then case-insensitively.
    /// A path inside one of <paramref name="testRoots"/> is not testable.
    /// </summary>
    public static LocateResult Locate(IReadOnlyList<ClassCandidate> classes, string query, IReadOnlyCollection<string>? testRoots = null)
    {
        query = query.Trim();
        if (query.EndsWith(".java", StringComparison.OrdinalIgnoreCase) || query.Contains('/', StringComparison.Ordinal) || query.Contains('\\', StringComparison.Ordinal))
        {
            var path = BuildLayout.Normalize(query);
            if (testRoots?.Any(t => path.StartsWith(t + "/", StringComparison.Ordinal)) == true || path.Contains("/src/test/", StringComparison.Ordinal) || path.StartsWith("src/test/", StringComparison.Ordinal))
            {
                return new LocateResult.Failed(ErrorCodes.B1CutNotTestable, $"{path} nằm trong thư mục test", []);
            }

            // A bare file name ("Order.java") matches that file in any folder.
            var bare = !path.Contains('/', StringComparison.Ordinal);
            var inFile = classes.Where(c => string.Equals(c.Path, path, PathComparison) || bare && c.Path.EndsWith("/" + path, PathComparison)).ToList();
            var fileName = Path.GetFileNameWithoutExtension(path);
            var primaries = inFile.Where(c => c.SimpleName == fileName).ToList();
            var primary = primaries.Count == 1 ? primaries[0] : inFile.Count == 1 ? inFile[0] : null;
            return primary is not null
                ? Check(primary)
                : primaries.Count > 1 ? new LocateResult.Ambiguous(primaries.OrderBy(c => c.Fqn, StringComparer.Ordinal).ToList())
                : inFile.Count > 1
                    ? new LocateResult.Ambiguous(inFile)
                    : new LocateResult.Failed(ErrorCodes.B1CutMissing, $"không có class nào trong {path}", Suggest(classes, fileName));
        }

        if (query.Contains('.', StringComparison.Ordinal) || query.Contains('$', StringComparison.Ordinal))
        {
            var fqn = query.Replace('$', '.');
            var exact = classes.FirstOrDefault(c => c.Fqn == fqn);
            if (exact is not null)
            {
                return Check(exact);
            }

            // A nested class: an outer top-level class is a prefix of the name.
            var outer = classes.Where(c => fqn.StartsWith(c.Fqn + ".", StringComparison.Ordinal)).OrderByDescending(c => c.Fqn.Length).FirstOrDefault();
            if (outer is not null)
            {
                return new LocateResult.Failed(ErrorCodes.B1CutNotTestable, $"{query} là class lồng; hãy chọn class ngoài {outer.Fqn}", [outer.Fqn]);
            }

            return new LocateResult.Failed(ErrorCodes.B1CutMissing, $"không có class {query}", Suggest(classes, fqn[(fqn.LastIndexOf('.') + 1)..]));
        }

        var matches = classes.Where(c => c.SimpleName == query).ToList();
        if (matches.Count == 0)
        {
            matches = classes.Where(c => string.Equals(c.SimpleName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return matches.Count switch
        {
            0 => new LocateResult.Failed(ErrorCodes.B1CutMissing, $"không có class {query}", Suggest(classes, query)),
            1 => Check(matches[0]),
            _ => new LocateResult.Ambiguous(matches.OrderBy(c => c.Fqn, StringComparer.Ordinal).ToList()),
        };
    }

    /// <summary>
    /// <c>--package</c> (P01 §3.3 step 6): concrete classes (<c>kind = class</c>, not abstract) whose package is exactly
    /// <paramref name="packageName"/>, sub-packages excluded, sorted by FQN.
    /// </summary>
    public static IReadOnlyList<ClassCandidate> InPackage(IReadOnlyList<ClassCandidate> classes, string packageName) =>
        classes.Where(c => c.Kind == CutKind.Class && !c.IsAbstract && c.Package == packageName.Trim())
            .GroupBy(c => c.Fqn).Select(g => g.First())
            .OrderBy(c => c.Fqn, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Kind check of P01 §3.3 step 5: interfaces and annotations are not testable; abstract classes, enums and records run with a
    /// warning.
    /// </summary>
    public static LocateResult Check(ClassCandidate candidate) => candidate switch
    {
        { Kind: CutKind.Interface or CutKind.Annotation } =>
            new LocateResult.Failed(ErrorCodes.B1CutNotTestable, $"{candidate.Fqn} là {(candidate.Kind == CutKind.Interface ? "interface" : "annotation")}", []),
        { Kind: CutKind.Class, IsAbstract: true } => new LocateResult.Found(candidate, $"{candidate.SimpleName} là class abstract, khó test trực tiếp"),
        { Kind: CutKind.Enum } => new LocateResult.Found(candidate, $"{candidate.SimpleName} là enum, khó test trực tiếp"),
        { Kind: CutKind.Record } => new LocateResult.Found(candidate, $"{candidate.SimpleName} là record, khó test trực tiếp"),
        _ => new LocateResult.Found(candidate, null),
    };

    /// <summary>
    /// Up to <see cref="MaxSuggestions"/> FQNs whose simple name is within Levenshtein distance 2 of <paramref name="name"/>, or contains
    /// it, ignoring case; closest first.
    /// </summary>
    public static IReadOnlyList<string> Suggest(IReadOnlyList<ClassCandidate> classes, string name)
    {
        if (name.Length == 0)
        {
            return [];
        }

        var lower = name.ToLowerInvariant();
        return classes
            .Select(c => (c.Fqn, Distance: Levenshtein(c.SimpleName.ToLowerInvariant(), lower), Contains: c.SimpleName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c.Distance <= 2 || c.Contains)
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Fqn, StringComparer.Ordinal)
            .Select(c => c.Fqn)
            .Distinct()
            .Take(MaxSuggestions)
            .ToList();
    }

    /// <summary>Edit distance between two strings.</summary>
    public static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
