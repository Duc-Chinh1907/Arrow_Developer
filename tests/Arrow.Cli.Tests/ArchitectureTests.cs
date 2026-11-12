using System.Xml.Linq;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Arrow.Cli.Tests;

/// <summary>
/// Layer rules of ARCHITECTURE §3.2 and P11 §4: <c>Cli → Business → DataAccess</c>, all three use <c>Common</c>,
/// no reference goes back up, and Cli touches DataAccess only in <see cref="CompositionRoot"/>.
/// Rewritten from <c>ARROW_Framework/tests/Arrow.Service.Tests/ArchitectureTests.cs</c> for the new layers.
/// </summary>
public class ArchitectureTests
{
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Arrow.Common"] = [],
        ["Arrow.DataAccess"] = ["Arrow.Common"],
        ["Arrow.Business"] = ["Arrow.Common", "Arrow.DataAccess"],
        ["Arrow.Cli"] = ["Arrow.Business", "Arrow.Common", "Arrow.DataAccess"],
    };

    [Fact]
    public void SourceProjectsReferenceOnlyLowerLayers()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var actual = Directory.GetDirectories(srcDir)
            .Select(dir => Path.Combine(dir, Path.GetFileName(dir) + ".csproj"))
            .Where(File.Exists)
            .ToDictionary(csproj => Path.GetFileNameWithoutExtension(csproj), ProjectReferences);

        Assert.Equal(AllowedProjectReferences.Keys.Order(), actual.Keys.Order());
        foreach (var (project, references) in actual)
        {
            Assert.True(
                AllowedProjectReferences[project].SequenceEqual(references),
                $"{project} references [{string.Join(", ", references)}], allowed [{string.Join(", ", AllowedProjectReferences[project])}]");
        }
    }

    [Fact]
    public void CliUsesDataAccessOnlyInCompositionRoot()
    {
        var result = Types.InAssembly(typeof(CompositionRoot).Assembly)
            .That().MeetCustomRule(new OutsideCompositionRoot())
            .Should().NotHaveDependencyOnAny("Arrow.DataAccess")
            .GetResult();

        Assert.True(result.IsSuccessful, "Cli types using Arrow.DataAccess: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName)));
    }

    private static string[] ProjectReferences(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ARROW.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ARROW.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Every type except <see cref="CompositionRoot"/> and the types nested in it (lambdas, closures).</summary>
    private sealed class OutsideCompositionRoot : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            for (var t = type; t is not null; t = t.DeclaringType)
            {
                if (t.FullName == typeof(CompositionRoot).FullName)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
