using Arrow.Common.Analysis;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Analysis;

/// <summary>A way to build the module of the CUT (P01 §4.1).</summary>
/// <param name="Tool">Maven or Gradle.</param>
/// <param name="BuildRoot">Folder the commands run from, relative to the project root.</param>
/// <param name="Mode">Reactor or standalone.</param>
/// <param name="Module">Maven <c>-pl</c> path (reactor) or Gradle project path; null for a Maven standalone build.</param>
/// <param name="Source"><c>detected:reactor</c> or <c>detected:standalone</c>.</param>
public sealed record BuildPlan(BuildToolKind Tool, string BuildRoot, BuildMode Mode, string? Module, string Source)
{
    /// <summary>Folder of the module according to this plan; for Gradle, the default folder of the project path.</summary>
    public string ExpectedModuleRoot => Tool == BuildToolKind.Gradle
        ? BuildLayout.Join(BuildRoot, (Module ?? ":").Trim(':').Replace(':', '/'))
        : Mode == BuildMode.Reactor && Module is not null ? BuildLayout.Join(BuildRoot, Module) : BuildLayout.Normalize(BuildRoot);
}

/// <summary>
/// Build root, module selector and module root (P01 §4.1, R01 §4.1 step 2). Paths are relative to the project root and use <c>/</c>.
/// </summary>
public static class BuildRootResolver
{
    /// <summary>
    /// Module root of a source root: the parent of <c>src/main/java</c>; for a declared source root, the nearest folder above it with a
    /// build file. Null when neither applies.
    /// </summary>
    public static string? ModuleRoot(string sourceRoot, BuildLayout layout)
    {
        var root = BuildLayout.Normalize(sourceRoot);
        if (ModuleRootFromSourceRoot(root) is { } standard)
        {
            return standard;
        }

        for (var dir = Parent(root); dir is not null; dir = Parent(dir))
        {
            if (layout.Find(dir) is not null)
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>Module root of a standard source root <c>…/src/main/java</c>; null for any other source root.</summary>
    public static string? ModuleRootFromSourceRoot(string sourceRoot) =>
        sourceRoot == "src/main/java" ? "."
        : sourceRoot.EndsWith("/src/main/java", StringComparison.Ordinal) ? sourceRoot[..^"/src/main/java".Length]
        : null;

    /// <summary>
    /// The plans to try in order: the reactor plan when a folder above the module declares it as a module, then the standalone plan.
    /// Empty when there is no build file at or above the module root. <paramref name="warnings"/> receives the warning of P01 §4.2
    /// when a folder has both a POM and a Gradle build.
    /// </summary>
    public static IReadOnlyList<BuildPlan> Plans(BuildLayout layout, string moduleRoot, ICollection<string>? warnings = null)
    {
        moduleRoot = BuildLayout.Normalize(moduleRoot);
        var tool = ToolAt(layout, moduleRoot, warnings);
        if (tool is null)
        {
            return [];
        }

        var plans = new List<BuildPlan>();
        if (ReactorRoot(layout, moduleRoot, tool.Value) is { } reactor)
        {
            var module = RelativeTo(reactor, moduleRoot)!;
            plans.Add(new BuildPlan(
                tool.Value,
                reactor,
                BuildMode.Reactor,
                tool == BuildToolKind.Gradle ? ":" + module.Replace('/', ':') : module,
                "detected:reactor"));
        }

        var own = layout.Find(moduleRoot);
        if (tool == BuildToolKind.Maven ? own?.HasPom == true : own?.HasGradle == true)
        {
            plans.Add(new BuildPlan(tool.Value, moduleRoot, BuildMode.Standalone, tool == BuildToolKind.Gradle ? ":" : null, "detected:standalone"));
        }

        return plans;
    }

    /// <summary>
    /// The highest folder above <paramref name="moduleRoot"/> that declares it as a module: through <c>&lt;modules&gt;</c>, recursively,
    /// for Maven; through <c>include</c> of its settings file for Gradle. Null when none does.
    /// </summary>
    public static string? ReactorRoot(BuildLayout layout, string moduleRoot, BuildToolKind tool)
    {
        string? highest = null;
        for (var dir = Parent(moduleRoot); dir is not null; dir = Parent(dir))
        {
            var found = layout.Find(dir);
            var declares = tool == BuildToolKind.Maven
                ? found is { HasPom: true } && MavenModulesOf(layout, dir).Contains(moduleRoot)
                : found is { HasGradleSettings: true } && found.GradleIncludes.Contains(moduleRoot);
            if (declares)
            {
                highest = dir;
            }
        }

        return highest;
    }

    /// <summary>Every module below the POM at <paramref name="dir"/>, following nested <c>&lt;modules&gt;</c>.</summary>
    public static IReadOnlyList<string> MavenModulesOf(BuildLayout layout, string dir)
    {
        var result = new List<string>();
        var queue = new Queue<string>([dir]);
        while (queue.Count > 0 && result.Count < 1000)
        {
            foreach (var module in layout.Find(queue.Dequeue())?.MavenModules ?? [])
            {
                if (!result.Contains(module))
                {
                    result.Add(module);
                    queue.Enqueue(module);
                }
            }
        }

        return result;
    }

    /// <summary>Modules of the build rooted at <paramref name="buildRoot"/> (P01 §4.6), relative to the project root.</summary>
    public static IReadOnlyList<string> Modules(BuildLayout layout, string buildRoot, BuildToolKind tool) =>
        tool == BuildToolKind.Maven
            ? MavenModulesOf(layout, buildRoot)
            : layout.Find(buildRoot)?.GradleIncludes ?? [];

    /// <summary>
    /// Gradle project that owns a source root: the project whose folder is the longest prefix of it; the root project when no other
    /// matches. <paramref name="projects"/> come from the init script, with absolute folders below <paramref name="buildRootAbsolute"/>.
    /// </summary>
    public static string? GradleOwner(IReadOnlyList<GradleProject> projects, string buildRootAbsolute, string sourceRootFromBuildRoot) =>
        projects
            .Select(p => (p.Path, Dir: RelativeTo(buildRootAbsolute, p.Dir)))
            .Where(p => p.Dir is not null && (p.Dir == "." || sourceRootFromBuildRoot == p.Dir || sourceRootFromBuildRoot.StartsWith(p.Dir + "/", StringComparison.Ordinal)))
            .OrderByDescending(p => p.Dir == "." ? 0 : p.Dir!.Length)
            .Select(p => p.Path)
            .FirstOrDefault();

    /// <summary>
    /// The CUT is inside the expected module root: for a standard source root the module root it implies must be the expected one;
    /// for another source root it only has to lie below it.
    /// </summary>
    public static bool ModuleMatches(string sourceRoot, string expectedModuleRoot)
    {
        if (ModuleRootFromSourceRoot(sourceRoot) is { } fromCut)
        {
            return fromCut == expectedModuleRoot;
        }

        return expectedModuleRoot == "." || sourceRoot == expectedModuleRoot || sourceRoot.StartsWith(expectedModuleRoot + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="child"/> relative to <paramref name="root"/> with <c>/</c>; <c>.</c> when equal; null when it is not inside.
    /// Both may be absolute (any separator) or relative.
    /// </summary>
    public static string? RelativeTo(string root, string child)
    {
        var r = root.Replace('\\', '/').TrimEnd('/');
        var c = child.Replace('\\', '/').TrimEnd('/');
        if (r == "." || r.Length == 0)
        {
            return c.StartsWith('/') || c.Contains(':', StringComparison.Ordinal) ? null : BuildLayout.Normalize(c);
        }

        if (string.Equals(r, c, StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        return c.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ? c[(r.Length + 1)..] : null;
    }

    private static BuildToolKind? ToolAt(BuildLayout layout, string moduleRoot, ICollection<string>? warnings)
    {
        for (var dir = moduleRoot; dir is not null; dir = Parent(dir))
        {
            if (layout.Find(dir) is not { } found)
            {
                continue;
            }

            if (found.HasPom && found.HasGradle)
            {
                warnings?.Add($"{(dir == "." ? "Gốc project" : dir)} có cả pom.xml và file Gradle; ARROW dùng Maven.");
            }

            return found.HasPom ? BuildToolKind.Maven : BuildToolKind.Gradle;
        }

        return null;
    }

    private static string? Parent(string path)
    {
        if (path == ".")
        {
            return null;
        }

        var slash = path.LastIndexOf('/');
        return slash < 0 ? "." : path[..slash];
    }
}
