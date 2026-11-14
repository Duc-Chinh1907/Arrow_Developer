using Arrow.Business.Analysis;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Tests.Analysis;

public sealed class SourceRootsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-roots-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() => Directory.Delete(_root, true);

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative.EndsWith("settings.gradle", StringComparison.Ordinal) ? "include 'app', 'api'\ninclude ':libs:util'" : "");
    }

    // GD7 (otroslogviewer): subprojects configured from the root build.gradle have no build file; their classes were not listed.
    [Fact]
    public void GradleSubprojectsWithoutABuildFileAreModules()
    {
        Touch("settings.gradle");
        Touch("build.gradle");
        Touch("app/src/main/java/a/App.java");
        Touch("app/src/test/java/a/AppTest.java");
        Touch("api/src/main/java/a/Api.java");
        Touch("libs/util/src/main/java/a/Util.java");
        Touch("notincluded/src/main/java/a/Other.java");

        var layout = BuildLayout.Scan(_root);

        Assert.Equal(["app/src/main/java", "api/src/main/java", "libs/util/src/main/java"], ProjectAnalyzer.SourceRoots(_root, layout, "src/main/java"));
        Assert.Equal(["app/src/test/java"], ProjectAnalyzer.SourceRoots(_root, layout, "src/test/java"));
    }

    [Fact]
    public void FoldersWithABuildFileAndTheRootStillCount()
    {
        Touch("pom.xml");
        Touch("src/main/java/a/Root.java");
        Touch("tools/x/pom.xml");
        Touch("tools/x/src/main/java/a/X.java");

        Assert.Equal(["src/main/java", "tools/x/src/main/java"], ProjectAnalyzer.SourceRoots(_root, BuildLayout.Scan(_root), "src/main/java"));
    }
}
