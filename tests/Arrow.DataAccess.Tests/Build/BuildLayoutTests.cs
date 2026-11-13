using Arrow.DataAccess.Build;

namespace Arrow.DataAccess.Tests.Build;

/// <summary>Build files and declared modules of a project, read without a build (P01 §3.3, §4.1, §4.6).</summary>
public class BuildLayoutTests
{
    [Fact]
    public void ScansBuildFolders()
    {
        using var temp = new TempDir();
        void Write(string relative, string content = "")
        {
            var path = Path.Combine(temp.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Write("pom.xml", "<project><modules><module>shop-api</module><module>./shop-core/</module><module>tools/pom-tools.xml</module></modules></project>");
        Write("shop-api/pom.xml", "<project/>");
        Write("shop-core/pom.xml", "<project/>");
        Write("shop-core/target/classes/pom.xml", "<project/>");
        Write("shop-core/src/main/java/a/pom.xml", "<project/>");
        Write("tools/pom-tools.xml", "<project/>");
        Write("samples/standalone/pom.xml", "<project/>");
        Write("node_modules/x/pom.xml", "<project/>");
        Write("gradle-part/settings.gradle.kts", "include(\":app\")");
        Write("src/it/mod-alpha/pom.xml", "<project/>");

        var layout = BuildLayout.Scan(temp.Root);
        Assert.Equal([".", "gradle-part", "samples/standalone", "shop-api", "shop-core", "src/it/mod-alpha"], layout.Dirs.Select(d => d.Path));
        Assert.Equal(["shop-api", "shop-core", "tools"], layout.Find(".")!.MavenModules);
        Assert.True(layout.Find("gradle-part")!.HasGradleSettings);
        Assert.False(layout.Find("gradle-part")!.HasGradleBuild);
        Assert.Equal(["gradle-part/app"], layout.Find("gradle-part")!.GradleIncludes);
        Assert.Null(layout.Find("node_modules/x"));
    }

    [Fact]
    public void ReadsGradleIncludesInEveryForm()
    {
        const string groovy = """
            rootProject.name = 'shop'
            include 'core', ':api'
            include ':dbfit-java:core'
            includeBuild 'build-logic'
            // include 'commented'
            include(
                "multi-a",
                "multi-b"
            )
            """;
        Assert.Equal(["core", "api", "dbfit-java/core", "multi-a", "multi-b"], BuildLayout.GradleIncludes(groovy));
        Assert.Equal(["app", "lib/x"], BuildLayout.GradleIncludes("include(\":app\", \":lib:x\")"));
    }

    [Fact]
    public void MavenModulesIgnoreBrokenXmlAndProfiles()
    {
        Assert.Empty(BuildLayout.MavenModules("<project><modules>"));
        Assert.Equal(["a"], BuildLayout.MavenModules("<project><modules><module>a</module></modules><profiles><profile><modules><module>b</module></modules></profile></profiles></project>"));
    }

    [Theory]
    [InlineData("./a//b/", "a/b")]
    [InlineData("a\\b", "a/b")]
    [InlineData("", ".")]
    [InlineData("a/../b", "b")]
    [InlineData("../x", "../x")]
    public void NormalizesRelativePaths(string input, string expected) => Assert.Equal(expected, BuildLayout.Normalize(input));
}
