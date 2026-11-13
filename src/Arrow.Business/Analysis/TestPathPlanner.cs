using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Analysis;

/// <summary>Where the generated test goes (P01 §4.7).</summary>
/// <param name="Path">Relative to the project root, e.g. <c>core/src/test/java/a/Order_ArrowTest.java</c>.</param>
/// <param name="Fqn">Test class FQN.</param>
/// <param name="NameIndex">k of <c>_Arrow&lt;k&gt;Test</c>; 1 for <c>_ArrowTest</c>.</param>
public sealed record PlannedTestPath(string Path, string Fqn, int NameIndex);

/// <summary>
/// Name of the generated test (P01 §4.7, decided 10/11/2026, different from R01 §4.7): <c>&lt;CUT&gt;_ArrowTest</c>, and on a collision
/// <c>&lt;CUT&gt;_Arrow&lt;k&gt;Test</c> with k = 2…99, so that every name still ends in <c>Test</c> and matches the default test patterns.
/// </summary>
public static class TestPathPlanner
{
    /// <summary>Largest k tried.</summary>
    public const int MaxIndex = 99;

    /// <summary>Simple name for index <paramref name="k"/>: 1 → <c>Order_ArrowTest</c>, 2 → <c>Order_Arrow2Test</c>.</summary>
    public static string TestName(string cutSimpleName, int k) =>
        k <= 1 ? cutSimpleName + "_ArrowTest" : cutSimpleName + "_Arrow" + k.ToString(CultureInfo.InvariantCulture) + "Test";

    /// <summary>
    /// The first free name. <paramref name="taken"/> gets the candidate path and simple name and returns true when the file already
    /// exists in the user's project or the simple name is already a test class of the reactor. Null when k = 99 is taken too
    /// (<c>B1_TEST_NAME_EXHAUSTED</c>).
    /// </summary>
    public static PlannedTestPath? Plan(string testRoot, string cutFqn, Func<string, string, bool> taken)
    {
        var (package, simple) = ProjectPaths.SplitFqn(cutFqn);
        for (var k = 1; k <= MaxIndex; k++)
        {
            var name = TestName(simple, k);
            var path = BuildLayout.Join(testRoot, (package.Length == 0 ? "" : package.Replace('.', '/') + "/") + name + ".java");
            if (!taken(path, name))
            {
                return new PlannedTestPath(path, package.Length == 0 ? name : $"{package}.{name}", k);
            }
        }

        return null;
    }
}

/// <summary>
/// Test name patterns of the project (P01 §4.7 step 4): Surefire <c>&lt;includes&gt;</c>/<c>&lt;excludes&gt;</c> or the filters of the
/// Gradle <c>test</c> task. Patterns are Ant style (<c>**/*Test.java</c>) or Surefire <c>%regex[…]</c>, relative to the test root.
/// <c>.java</c> and <c>.class</c> are treated alike.
/// </summary>
public static class TestNamePatterns
{
    /// <summary>
    /// True when <paramref name="pathInTestRoot"/> (e.g. <c>a/b/Order_ArrowTest.java</c>) matches an include and no exclude.
    /// No include means every class is included (Gradle without filters).
    /// </summary>
    public static bool Matches(string pathInTestRoot, IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        var path = Strip(pathInTestRoot.Replace('\\', '/').TrimStart('/'));
        var included = includes.Count == 0 || includes.Any(p => Match(p, path));
        return included && !excludes.Any(p => Match(p, path));
    }

    private static bool Match(string pattern, string path)
    {
        pattern = pattern.Trim();
        if (pattern.StartsWith("%regex[", StringComparison.Ordinal) && pattern.EndsWith(']'))
        {
            var regex = pattern["%regex[".Length..^1];
            return Regex.IsMatch(path + ".class", "^" + regex + "$") || Regex.IsMatch(path + ".java", "^" + regex + "$") || Regex.IsMatch(path, "^" + regex + "$");
        }

        // Surefire also accepts a class name pattern without folders, e.g. "*Test": it applies to every folder.
        var glob = Strip(pattern.Replace('\\', '/').TrimStart('/'));
        if (!glob.Contains('/', StringComparison.Ordinal))
        {
            glob = "**/" + glob;
        }

        return Regex.IsMatch(path, "^" + GlobToRegex(glob) + "$");
    }

    private static string Strip(string path) =>
        path.EndsWith(".java", StringComparison.Ordinal) ? path[..^5]
        : path.EndsWith(".class", StringComparison.Ordinal) ? path[..^6]
        : path;

    private static string GlobToRegex(string glob)
    {
        var text = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" matches zero or more folders; "**" elsewhere matches anything.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    text.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    text.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
            {
                text.Append("[^/]*");
            }
            else if (c == '?')
            {
                text.Append("[^/]");
            }
            else
            {
                text.Append(Regex.Escape(c.ToString()));
            }
        }

        return text.ToString();
    }
}
