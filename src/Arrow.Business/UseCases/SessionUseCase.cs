using System.Text.RegularExpressions;
using Arrow.Business.Config;
using Arrow.Business.Environment;
using Arrow.Business.Llm;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.JavaHelper;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.UseCases;

/// <summary>The project of an interactive session, from a quick read of its build files (P08 §5.1).</summary>
/// <param name="Root">Project root.</param>
/// <param name="Name">Folder name.</param>
/// <param name="BuildTool"><c>Maven</c> or <c>Gradle</c>.</param>
/// <param name="Java">Declared Java version; null when not found.</param>
/// <param name="Framework"><c>JUnit 5</c>, <c>JUnit 4</c>, <c>TestNG</c>; null when not found.</param>
public sealed record SessionProject(string Root, string Name, string BuildTool, string? Java, string? Framework);

/// <summary>The model line of the banner.</summary>
/// <param name="Name">Model name; null when none is configured.</param>
/// <param name="Host">Provider host.</param>
/// <param name="Rounds">Repair budget N.</param>
/// <param name="Models">Every configured model name.</param>
public sealed record SessionModel(string? Name, string? Host, int Rounds, IReadOnlyList<string> Models);

/// <summary>A class of the class picker (P08 §5.3).</summary>
/// <param name="Fqn">FQN.</param>
/// <param name="HasTest">A <c>&lt;Name&gt;Test</c> or <c>&lt;Name&gt;_ArrowTest</c> exists in the project.</param>
public sealed record SessionClass(string Fqn, bool HasTest);

/// <summary>What the interactive session asks the business layer (P08 §5).</summary>
public interface ISessionUseCase
{
    /// <summary><c>ARROW_HOME/repl_history</c> (P08 §5.2).</summary>
    string HistoryFile { get; }

    /// <summary>The project at or above <paramref name="directory"/>; null when there is none.</summary>
    SessionProject? FindProject(string directory);

    /// <summary>The default model and the configured models.</summary>
    SessionModel Model(string? projectRoot);

    /// <summary>Concrete classes of every main source root of the project, by java-helper <c>list_classes</c>.</summary>
    Task<IReadOnlyList<SessionClass>> ListClassesAsync(string projectRoot, CancellationToken ct);
}

/// <summary>
/// <see cref="ISessionUseCase"/>. The banner reads build files only, without compiling (P08 §5.1); the class list comes from
/// java-helper on every <c>src/main/java</c> of the project.
/// </summary>
public sealed partial class SessionUseCase(IConfigStore config, ToolDiscovery tools, ArrowHome home) : ISessionUseCase
{
    /// <summary>Most classes listed for the picker.</summary>
    public const int MaxClasses = 5000;

    /// <summary>Most module build files read for the banner.</summary>
    public const int MaxModules = 20;

    private static readonly Dictionary<string, string?> NoOverrides = [];

    public string HistoryFile => home.ReplHistoryFile;

    [GeneratedRegex(@"<(?:maven\.compiler\.(?:release|source)|release|source|java\.version)>\s*(?:1\.)?(\d+)\s*<")]
    private static partial Regex MavenJava();

    [GeneratedRegex(@"(?:JavaLanguageVersion\.of\(\s*|sourceCompatibility\s*=\s*['""]?(?:JavaVersion\.VERSION_)?(?:1[._])?)(\d+)")]
    private static partial Regex GradleJava();

    public SessionProject? FindProject(string directory)
    {
        if (ProjectRootFinder.Find(directory) is not { } root)
        {
            return null;
        }

        var maven = File.Exists(Path.Combine(root, "pom.xml"));
        var text = BuildText(root, maven);
        var java = (maven ? MavenJava() : GradleJava()).Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        var framework = text.Contains("junit-jupiter", StringComparison.Ordinal) || text.Contains("useJUnitPlatform", StringComparison.Ordinal) ? "JUnit 5"
            : text.Contains("testng", StringComparison.OrdinalIgnoreCase) ? "TestNG"
            : text.Contains("junit", StringComparison.OrdinalIgnoreCase) ? "JUnit 4"
            : null;
        return new SessionProject(root, new DirectoryInfo(root).Name, maven ? "Maven" : "Gradle", java, framework);
    }

    public SessionModel Model(string? projectRoot)
    {
        var settings = config.Load(projectRoot, NoOverrides).Config;
        var models = settings.Llm.Models.Keys.Order(StringComparer.Ordinal).ToList();
        try
        {
            var model = ResolvedModel.From(settings, null);
            return new SessionModel(model.Name, model.BaseUrl.Host, settings.Repair.Rounds, models);
        }
        catch (ArrowException)
        {
            return new SessionModel(null, null, settings.Repair.Rounds, models);
        }
    }

    public async Task<IReadOnlyList<SessionClass>> ListClassesAsync(string projectRoot, CancellationToken ct)
    {
        var settings = config.Load(projectRoot, NoOverrides).Config;
        var machine = await tools.FindAsync(settings.Java, ct).ConfigureAwait(false);
        var java = JavaHelperJar.FindJava(machine.HelperJdkHomes);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 8 };
        var roots = Directory.EnumerateDirectories(projectRoot, "java", options)
            .Where(d => d.Replace('\\', '/').EndsWith("/src/main/java", StringComparison.Ordinal) && !IsOutput(projectRoot, d))
            .ToList();
        var testNames = Directory.EnumerateDirectories(projectRoot, "java", options)
            .Where(d => d.Replace('\\', '/').EndsWith("/src/test/java", StringComparison.Ordinal))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.java", options))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        home.EnsureCreated();
        await using var pool = new JavaHelperPool(new JavaHelperOptions(java, JavaHelperJar.Ensure(home)), 1);
        using var lease = await pool.RentAsync(ct).ConfigureAwait(false);
        var listed = await lease.Helper.ListClassesAsync(roots, MaxClasses, ct).ConfigureAwait(false);
        return listed.Classes
            .Where(c => c.Kind is CutKind.Class or CutKind.Enum or CutKind.Record && !c.IsAbstract)
            .Select(c => new SessionClass(c.Fqn, testNames.Contains(c.SimpleName + "Test") || testNames.Contains(c.SimpleName + "_ArrowTest")))
            .DistinctBy(c => c.Fqn)
            .OrderBy(c => c.Fqn, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The build files of the root, then of the modules one folder below (at most <see cref="MaxModules"/>): the Java version and
    /// the test framework of a multi-module project are often declared only in a module.
    /// </summary>
    private static string BuildText(string root, bool maven)
    {
        string[] names = maven ? ["pom.xml"] : ["build.gradle", "build.gradle.kts"];
        IEnumerable<string> Files(string dir) => names.Select(n => Path.Combine(dir, n)).Where(File.Exists);
        var modules = Directory.EnumerateDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && !IsOutput(root, d))
            .Order(StringComparer.Ordinal)
            .SelectMany(Files)
            .Take(MaxModules);
        var text = new System.Text.StringBuilder();
        foreach (var file in Files(root).Concat(modules))
        {
            try
            {
                text.Append(File.ReadAllText(file)).Append('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The banner is a hint; an unreadable module file only leaves a field empty.
            }
        }

        return text.ToString();
    }

    private static bool IsOutput(string root, string dir) =>
        Path.GetRelativePath(root, dir).Replace('\\', '/').Split('/').Any(p => p is "target" or "build" or "node_modules" or ".git");
}
