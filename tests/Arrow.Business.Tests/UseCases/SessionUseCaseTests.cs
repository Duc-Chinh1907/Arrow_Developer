using Arrow.Business.UseCases;

namespace Arrow.Business.Tests.UseCases;

public sealed class SessionUseCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arrow-session-" + Guid.NewGuid().ToString("N"));

    // FindProject reads build files only; the config, tools and home are used by the other members.
    private readonly SessionUseCase _session = new(null!, null!, null!);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Maven_project_is_found_from_a_source_folder()
    {
        Write("shop/pom.xml", "<project><properties><maven.compiler.source>1.8</maven.compiler.source></properties>"
            + "<dependencies><dependency><artifactId>junit</artifactId></dependency></dependencies></project>");
        var source = Path.Combine(_dir, "shop", "src", "main", "java", "com");
        Directory.CreateDirectory(source);

        var project = _session.FindProject(source);

        Assert.Equal(new SessionProject(Path.Combine(_dir, "shop"), "shop", "Maven", "8", "JUnit 4"), project);
    }

    [Fact]
    public void Multi_module_gradle_reads_the_module_build_files()
    {
        Write("app/settings.gradle", "include 'core'");
        Write("app/core/build.gradle", "java { toolchain { languageVersion = JavaLanguageVersion.of(17) } }\ntest { useJUnitPlatform() }");
        Write("app/build/build.gradle", "sourceCompatibility = 8");

        var project = _session.FindProject(Path.Combine(_dir, "app"));

        Assert.Equal(new SessionProject(Path.Combine(_dir, "app"), "app", "Gradle", "17", "JUnit 5"), project);
    }

    [Fact]
    public void Maven_testng_module_release()
    {
        Write("lib/pom.xml", "<project><modules><module>a</module></modules></project>");
        Write("lib/a/pom.xml", "<project><properties><maven.compiler.release>11</maven.compiler.release></properties><artifactId>testng</artifactId></project>");

        var project = _session.FindProject(Path.Combine(_dir, "lib"));

        Assert.Equal(("11", "TestNG"), (project?.Java, project?.Framework));
    }

    [Fact]
    public void Folder_without_build_file_has_no_project()
    {
        Directory.CreateDirectory(_dir);

        Assert.Null(_session.FindProject(_dir));
    }
}
