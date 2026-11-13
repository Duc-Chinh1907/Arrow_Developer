using System.Text;
using Arrow.Common.Analysis;

namespace Arrow.Business.Analysis;

/// <summary>
/// The text form of M, <c>metadata_prompt.txt</c> (P01 §5.2), filled into <c>{{repository_configuration}}</c> of the prompt (P02).
/// The text is in English, like the prompts.
/// </summary>
public static class MetadataPromptRenderer
{
    /// <summary>Dependencies listed at most; test libraries first.</summary>
    public const int MaxDependencies = 25;

    /// <summary>Modules listed at most.</summary>
    public const int MaxModules = 15;

    /// <summary>Seven lines, joined with <c>\n</c>, without a final line break.</summary>
    public static string Render(RepositoryMetadata m)
    {
        var text = new StringBuilder();
        var build = m.BuildTool;
        var structure = m.ProjectStructure;
        var tool = build.Kind == BuildToolKind.Gradle ? "Gradle" : "Maven";
        var scope = structure.MultiModule
            ? build.Kind == BuildToolKind.Gradle
                ? $"project {build.ReactorModule ?? ":"}, multi-project build"
                : $"module {structure.TargetModule}, multi-module project"
            : "single-module project";
        text.Append($"- Build tool: {tool}{(build.Version.Length > 0 ? " " + build.Version : "")} ({scope}, build root {build.BuildRoot})\n");

        if (m.Java is { } java)
        {
            var level = java.Major ?? java.JdkMajor;
            text.Append($"- Java version: {level} (compile with JDK {java.JdkMajor}; do not use language features or APIs newer than Java {level})\n");
        }
        else
        {
            text.Append("- Java version: unknown\n");
        }

        if (m.TestFramework is { Name: not null } framework)
        {
            var name = framework.Name switch { "junit5" => "JUnit 5", "testng" => "TestNG", _ => "JUnit 4" };
            var frameworkText = name + (framework.Version.Length > 0 ? $" ({framework.Version})" : "")
                                + (framework.Vintage ? ", JUnit 4 tests also run through the vintage engine" : "");
            var mocking = framework.Mocking.Count == 0 ? "none" : string.Join(", ", framework.Mocking.Select(Library).Distinct());
            var assertions = framework.AssertionLibs.Count == 0
                ? $"none beyond {(framework.Name == "testng" ? "TestNG" : "JUnit")}"
                : string.Join(", ", framework.AssertionLibs.Select(Library).Distinct());
            text.Append($"- Test framework: {frameworkText}; mocking: {mocking}; assertions: {assertions}\n");
        }
        else
        {
            text.Append("- Test framework: none found in the test dependencies\n");
        }

        var dependencies = m.Dependencies is null
            ? []
            : m.Dependencies.Test.Concat(m.Dependencies.Direct).Select(d => d.Coordinates).Distinct().ToList();
        var shown = dependencies.Take(MaxDependencies).ToList();
        var more = dependencies.Count > shown.Count ? $", … (+{dependencies.Count - shown.Count} more)" : "";
        text.Append($"- Dependencies (direct): {(shown.Count == 0 ? "none" : string.Join(", ", shown) + more)}\n");

        var modules = structure.MultiModule
            ? "modules " + string.Join(", ", structure.Modules.Take(MaxModules)) + (structure.Modules.Count > MaxModules ? $", … (+{structure.Modules.Count - MaxModules} more)" : "")
            : "single module";
        text.Append($"- Project structure: {modules}; CUT package {(m.Cut.Package.Length == 0 ? "(default package)" : m.Cut.Package)}\n");
        text.Append($"- Source path: {m.Paths.SourceRoot}\n");
        text.Append($"- Test path: {m.Paths.GeneratedTestPath}");
        return text.ToString();
    }

    /// <summary><c>org.mockito:mockito-core:3.12.4</c> → <c>Mockito 3.12.4</c>.</summary>
    public static string Library(string coordinates)
    {
        var parts = coordinates.Split(':');
        var artifact = parts.Length > 1 ? parts[1] : coordinates;
        var version = parts.Length > 2 ? " " + parts[2] : "";
        var name = artifact switch
        {
            _ when artifact.StartsWith("mockito", StringComparison.Ordinal) => "Mockito",
            _ when artifact.StartsWith("powermock", StringComparison.Ordinal) => "PowerMock",
            "easymock" => "EasyMock",
            "jmockit" => "JMockit",
            "assertj-core" => "AssertJ",
            "truth" => "Truth",
            _ when artifact.StartsWith("hamcrest", StringComparison.Ordinal) => "Hamcrest",
            _ => artifact,
        };
        return name + version;
    }
}
