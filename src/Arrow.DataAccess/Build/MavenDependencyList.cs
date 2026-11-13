using Arrow.Common.Analysis;

namespace Arrow.DataAccess.Build;

/// <summary>Reads <c>arrow-deps.txt</c>, written by <c>dependency:list -DoutputAbsoluteArtifactFilename=true</c> (P01 §4.4).</summary>
public static class MavenDependencyList
{
    private static readonly HashSet<string> Scopes = ["compile", "provided", "runtime", "test", "system", "import"];

    /// <summary>
    /// A line is <c>group:artifact:type[:classifier]:version:scope[:path]</c>, possibly followed by <c> -- module x</c> or
    /// <c> (optional)</c>. A Windows path contains <c>:</c>, so everything after the scope is the path. The path may contain
    /// spaces (a user folder such as <c>C:\Users\Ann Lee</c>); the coordinates may not.
    /// </summary>
    public static IReadOnlyList<ResolvedDependency> Parse(string text)
    {
        var result = new List<ResolvedDependency>();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            var module = line.IndexOf(" -- ", StringComparison.Ordinal);
            if (module >= 0)
            {
                line = line[..module];
            }

            line = line.Replace(" (optional)", "", StringComparison.Ordinal).Trim();
            var tokens = line.Split(':');
            if (tokens.Length < 5)
            {
                continue;
            }

            var scopeIndex = Enumerable.Range(4, tokens.Length - 4).FirstOrDefault(i => Scopes.Contains(tokens[i]), -1);
            if (scopeIndex < 0 || tokens[..(scopeIndex + 1)].Any(t => t.Length == 0 || t.Any(char.IsWhiteSpace)))
            {
                continue;
            }

            var path = scopeIndex + 1 < tokens.Length ? string.Join(':', tokens[(scopeIndex + 1)..]) : null;
            result.Add(new ResolvedDependency(tokens[0], tokens[1], tokens[scopeIndex - 1], tokens[scopeIndex], string.IsNullOrEmpty(path) ? null : path));
        }

        return result;
    }
}
