using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Arrow.DataAccess.Build;

/// <summary>A folder of the project that has a build file. Paths are relative to the project root and use <c>/</c>.</summary>
/// <param name="Path">The folder, <c>.</c> for the project root.</param>
/// <param name="HasPom">Has <c>pom.xml</c>.</param>
/// <param name="HasGradleBuild">Has <c>build.gradle</c> or <c>build.gradle.kts</c>.</param>
/// <param name="HasGradleSettings">Has <c>settings.gradle</c> or <c>settings.gradle.kts</c>.</param>
/// <param name="MavenModules">Folders of the <c>&lt;modules&gt;</c> of its <c>pom.xml</c> (not recursive).</param>
/// <param name="GradleIncludes">Folders of the projects its settings file includes, by default location.</param>
public sealed record BuildDir(
    string Path,
    bool HasPom,
    bool HasGradleBuild,
    bool HasGradleSettings,
    IReadOnlyList<string> MavenModules,
    IReadOnlyList<string> GradleIncludes)
{
    /// <summary>Has any Gradle build file.</summary>
    public bool HasGradle => HasGradleBuild || HasGradleSettings;
}

/// <summary>
/// The build files of a project, read without running a build: which folders have a build file and which modules they declare
/// (P01 §3.3 step 1, §4.1, §4.6). Build output and tool folders are not visited.
/// </summary>
public sealed partial class BuildLayout
{
    /// <summary>Folders searched below the project root (P01 §3.3).</summary>
    public const int MaxDepth = 6;

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "target", "build", "out", ".gradle", ".idea", ".vscode", "node_modules", ".arrow-work", ".mvn",
    };

    private readonly Dictionary<string, BuildDir> _dirs;

    public BuildLayout(IEnumerable<BuildDir> dirs)
    {
        _dirs = dirs.ToDictionary(d => d.Path, StringComparer.Ordinal);
    }

    /// <summary>Folders with a build file, sorted.</summary>
    public IReadOnlyList<BuildDir> Dirs => _dirs.Values.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();

    /// <summary>The folder at <paramref name="path"/>, or null when it has no build file.</summary>
    public BuildDir? Find(string path) => _dirs.GetValueOrDefault(path);

    /// <summary>Scans <paramref name="projectRoot"/> to <see cref="MaxDepth"/> levels.</summary>
    public static BuildLayout Scan(string projectRoot)
    {
        var root = System.IO.Path.GetFullPath(projectRoot);
        var dirs = new List<BuildDir>();
        Visit(root, ".", 0, dirs);
        return new BuildLayout(dirs);
    }

    private static void Visit(string root, string relative, int depth, List<BuildDir> dirs)
    {
        var dir = relative == "." ? root : System.IO.Path.Combine(root, relative);
        if (Read(dir, relative) is { } found)
        {
            dirs.Add(found);
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(dir).Where(c => (File.GetAttributes(c) & FileAttributes.ReparsePoint) == 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children.Order(StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(child);
            // src/main and src/test hold sources, never modules; src/it may hold sample modules, so src itself is visited.
            var sourceFolder = System.IO.Path.GetFileName(dir) == "src" && name is "main" or "test";
            if (!Skipped.Contains(name) && !name.StartsWith('.') && !sourceFolder)
            {
                Visit(root, Join(relative, name), depth + 1, dirs);
            }
        }
    }

    private static BuildDir? Read(string dir, string relative)
    {
        var pom = System.IO.Path.Combine(dir, "pom.xml");
        var hasPom = File.Exists(pom);
        var hasBuild = File.Exists(System.IO.Path.Combine(dir, "build.gradle")) || File.Exists(System.IO.Path.Combine(dir, "build.gradle.kts"));
        var settings = new[] { "settings.gradle", "settings.gradle.kts" }.Select(f => System.IO.Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        if (!hasPom && !hasBuild && settings is null)
        {
            return null;
        }

        var modules = hasPom ? MavenModules(File.ReadAllText(pom)).Select(m => Join(relative, m)).Where(m => !m.StartsWith("..", StringComparison.Ordinal)).Distinct().ToList() : [];
        var includes = settings is null ? [] : GradleIncludes(File.ReadAllText(settings)).Select(p => Join(relative, p)).Distinct().ToList();
        return new BuildDir(relative, hasPom, hasBuild, settings is not null, modules, includes);
    }

    /// <summary>
    /// Folders of the top-level <c>&lt;modules&gt;</c> of a POM (profiles are ignored). A module given as a path to a POM file
    /// (<c>core/pom-core.xml</c>) counts as its folder. Text that is not XML gives no module.
    /// </summary>
    public static IReadOnlyList<string> MavenModules(string pomXml)
    {
        try
        {
            var project = XDocument.Parse(pomXml).Root;
            return project?.Elements().Where(e => e.Name.LocalName == "modules")
                       .SelectMany(m => m.Elements().Where(e => e.Name.LocalName == "module"))
                       .Select(m => m.Value.Trim())
                       .Where(m => m.Length > 0)
                       .Select(m => m.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetDirectoryName(m.Replace('\\', '/'))?.Replace('\\', '/') ?? "." : m)
                       .Select(Normalize)
                       .ToList()
                   ?? [];
        }
        catch (XmlException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"\binclude\b(?!Build|Flat)\s*(\((?<args>[^)]*)\)|(?<args>[^\n]*(?:,\s*\n[^\n]*)*))")]
    private static partial Regex IncludeCall();

    [GeneratedRegex("""['"](?<name>[^'"\s]+)['"]""")]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>
    /// Folders of the projects in <c>include …</c> of a Gradle settings file, Groovy or Kotlin: <c>include 'a', 'b:c'</c>,
    /// <c>include(":a")</c>, or an argument list over several lines. <c>:b:c</c> → <c>b/c</c>.
    /// </summary>
    public static IReadOnlyList<string> GradleIncludes(string settings)
    {
        var text = Comments().Replace(settings.Replace("\r\n", "\n", StringComparison.Ordinal), "");
        return IncludeCall().Matches(text)
            .SelectMany(m => QuotedName().Matches(m.Groups["args"].Value))
            .Select(n => Normalize(n.Groups["name"].Value.Trim(':').Replace(':', '/')))
            .Where(n => n != ".")
            .Distinct()
            .ToList();
    }

    /// <summary>Relative path with <c>/</c>, without <c>./</c>, empty parts or trailing <c>/</c>; <c>.</c> when empty. <c>a/../b</c> → <c>b</c>.</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }

        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    /// <summary>Joins relative paths: <c>Join(".", "core")</c> → <c>core</c>.</summary>
    public static string Join(string left, string right) => Normalize(left + "/" + right);
}
