using System.Xml;
using System.Xml.Linq;
using Arrow.Common.Analysis;

namespace Arrow.DataAccess.Build;

/// <summary>One <c>&lt;project&gt;</c> of an effective POM. Folders are absolute paths of the build copy.</summary>
/// <param name="GroupId">groupId.</param>
/// <param name="ArtifactId">artifactId.</param>
/// <param name="SourceDirectory"><c>build/sourceDirectory</c>.</param>
/// <param name="TestSourceDirectory"><c>build/testSourceDirectory</c>.</param>
/// <param name="BuildDirectory"><c>build/directory</c>, usually <c>…/target</c>.</param>
/// <param name="Declared">Declared Java level as written; null when none is usable.</param>
/// <param name="DeclaredSource">Where <paramref name="Declared"/> was found.</param>
/// <param name="Dependencies">Direct dependencies; a missing scope is <c>compile</c>.</param>
/// <param name="TestIncludes"><c>&lt;includes&gt;</c> of <c>maven-surefire-plugin</c>; null when not configured.</param>
/// <param name="TestExcludes"><c>&lt;excludes&gt;</c> of <c>maven-surefire-plugin</c>; null when not configured.</param>
public sealed record MavenProject(
    string GroupId,
    string ArtifactId,
    string? SourceDirectory,
    string? TestSourceDirectory,
    string? BuildDirectory,
    string? Declared,
    string? DeclaredSource,
    IReadOnlyList<DependencyRef> Dependencies,
    IReadOnlyList<string>? TestIncludes,
    IReadOnlyList<string>? TestExcludes);

/// <summary>
/// Reads the file written by <c>help:effective-pom -Doutput=…</c>: one <c>&lt;project&gt;</c>, or several inside
/// <c>&lt;projects&gt;</c> when <c>-am</c> builds more modules (P01 §4.3, §4.4, §4.7).
/// </summary>
public static class EffectivePomReader
{
    /// <summary>Default Surefire includes when the POM sets none (P01 §4.7).</summary>
    public static IReadOnlyList<string> DefaultSurefireIncludes { get; } = ["**/Test*.java", "**/*Test.java", "**/*Tests.java", "**/*TestCase.java"];

    /// <summary>Parses the XML text; throws <see cref="XmlException"/> when it is not XML.</summary>
    public static IReadOnlyList<MavenProject> Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new XmlException("empty effective POM");
        var projects = root.Name.LocalName == "projects" ? Children(root, "project") : [root];
        return projects.Select(ReadProject).ToList();
    }

    /// <summary>
    /// The project of the module: the one whose <c>target</c> or <c>src/main/java</c> is directly inside
    /// <paramref name="moduleRootAbsolute"/>. Without a match, the last project, since Maven lists a module after the modules it depends on.
    /// </summary>
    public static MavenProject? SelectTarget(IReadOnlyList<MavenProject> projects, string moduleRootAbsolute)
    {
        var root = Normalize(moduleRootAbsolute);
        bool Owns(string? dir) => dir is not null
            && (string.Equals(Normalize(dir), root + "/target", PathComparison)
                || string.Equals(Normalize(dir), root + "/src/main/java", PathComparison));
        return projects.FirstOrDefault(p => Owns(p.BuildDirectory) || Owns(p.SourceDirectory)) ?? projects.LastOrDefault();
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static MavenProject ReadProject(XElement project)
    {
        var properties = Child(project, "properties");
        string? Property(string name) => Usable(Child(properties, name)?.Value);

        var build = Child(project, "build");
        var plugins = Children(Child(build, "plugins"), "plugin").ToList();
        var compiler = Child(Plugin(plugins, "maven-compiler-plugin"), "configuration");
        var surefire = Child(Plugin(plugins, "maven-surefire-plugin"), "configuration");

        // Order of P01 §4.3; a value that is not a Java level (such as an uninterpolated ${…}) is skipped.
        (string? Value, string Source)[] candidates =
        [
            (Property("maven.compiler.release"), "maven.compiler.release"),
            (Property("maven.compiler.source"), "maven.compiler.source"),
            (Usable(Child(compiler, "release")?.Value), "maven-compiler-plugin.release"),
            (Usable(Child(compiler, "source")?.Value), "maven-compiler-plugin.source"),
            (Property("java.version"), "java.version"),
        ];
        var declared = candidates.FirstOrDefault(c => JavaVersions.ParseLevel(c.Value) is not null);

        var dependencies = Children(Child(project, "dependencies"), "dependency")
            .Select(d => new DependencyRef(
                Text(d, "groupId"),
                Text(d, "artifactId"),
                Text(d, "version"),
                Text(d, "scope") is { Length: > 0 } scope ? scope : "compile"))
            .ToList();

        return new MavenProject(
            Text(project, "groupId"),
            Text(project, "artifactId"),
            Usable(Child(build, "sourceDirectory")?.Value),
            Usable(Child(build, "testSourceDirectory")?.Value),
            Usable(Child(build, "directory")?.Value),
            declared.Value,
            declared.Value is null ? null : declared.Source,
            dependencies,
            Patterns(surefire, "includes", "include"),
            Patterns(surefire, "excludes", "exclude"));
    }

    private static XElement? Plugin(IEnumerable<XElement> plugins, string artifactId) =>
        plugins.FirstOrDefault(p => Child(p, "artifactId")?.Value.Trim() == artifactId);

    private static List<string>? Patterns(XElement? configuration, string list, string item)
    {
        var container = Child(configuration, list);
        return container is null
            ? null
            : Children(container, item).Select(e => e.Value.Trim()).Where(v => v.Length > 0 && !v.Contains("${", StringComparison.Ordinal)).ToList();
    }

    private static string? Usable(string? value) =>
        value is null || value.Contains("${", StringComparison.Ordinal) || value.Trim().Length == 0 ? null : value.Trim();

    private static string Text(XElement parent, string name) => Child(parent, name)?.Value.Trim() ?? "";

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent is null ? [] : parent.Elements().Where(e => e.Name.LocalName == name);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
