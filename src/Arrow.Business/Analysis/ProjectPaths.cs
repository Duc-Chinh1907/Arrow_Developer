using System.Text.RegularExpressions;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Analysis;

/// <summary>Source roots, packages and names (P01 §4.6–4.7). Paths are relative to the project root and use <c>/</c>.</summary>
public static partial class ProjectPaths
{
    /// <summary><c>com.acme.Order</c> → (<c>com.acme</c>, <c>Order</c>); no package gives an empty package.</summary>
    public static (string Package, string SimpleName) SplitFqn(string fqn)
    {
        var dot = fqn.LastIndexOf('.');
        return dot < 0 ? ("", fqn) : (fqn[..dot], fqn[(dot + 1)..]);
    }

    /// <summary>
    /// <paramref name="cutPath"/> without <c>&lt;package path&gt;/&lt;File&gt;.java</c>. Only the package of <paramref name="fqn"/> is used, not
    /// its simple name: Java allows a top-level class in a file with another name (<c>NamedExtension</c> in <c>Extension.java</c>).
    /// When the folder does not end with the package path, the folder itself.
    /// </summary>
    public static string SourceRoot(string cutPath, string fqn)
    {
        var path = BuildLayout.Normalize(cutPath);
        var slash = path.LastIndexOf('/');
        var folder = slash < 0 ? "." : path[..slash];
        var package = SplitFqn(fqn).Package;
        if (package.Length == 0)
        {
            return folder;
        }

        var packagePath = package.Replace('.', '/');
        if (folder == packagePath)
        {
            return ".";
        }

        return folder.EndsWith("/" + packagePath, StringComparison.Ordinal) ? folder[..^(packagePath.Length + 1)] : folder;
    }

    /// <summary>Up to <paramref name="limit"/> packages: the CUT package, then its parents and children, then the others alphabetically.</summary>
    public static IReadOnlyList<string> RankPackages(IEnumerable<string> packages, string cutPackage, int limit = 30)
    {
        static bool Related(string a, string b) => a == b || a.StartsWith(b + ".", StringComparison.Ordinal) || b.StartsWith(a + ".", StringComparison.Ordinal);
        return packages.Distinct()
            .OrderBy(p => p == cutPackage ? 0 : Related(p, cutPackage) ? 1 : 2)
            .ThenBy(p => p, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    [GeneratedRegex(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>Package declared in a Java source, read with a regular expression (comments ignored); empty when none.</summary>
    public static string PackageOf(string source)
    {
        var match = PackageLine().Match(Comments().Replace(source, ""));
        return match.Success ? match.Groups[1].Value : "";
    }
}
