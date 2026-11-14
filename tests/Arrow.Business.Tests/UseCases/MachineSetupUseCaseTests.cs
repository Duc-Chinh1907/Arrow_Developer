using Arrow.Business.UseCases;

namespace Arrow.Business.Tests.UseCases;

public sealed class MachineSetupUseCaseTests : IDisposable
{
    private static readonly Dictionary<string, string?> NoOverrides = [];
    private readonly FakeMachine _machine = new();

    public void Dispose() => _machine.Dispose();

    private MachineSetupUseCase UseCase() => new(_machine.Config, new ConfigUseCase(_machine.Config, _machine.Home), _machine.Discovery);

    private string ManualJdk(string name, string version)
    {
        var home = Path.Combine(_machine.Root, "manual", name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        foreach (var file in new[] { "java", "java.exe", "javac", "javac.exe" })
        {
            File.WriteAllText(Path.Combine(home, "bin", file), "");
        }

        _machine.Runner.JavaVersions[home] = version;
        return home;
    }

    [Fact]
    public async Task FindsOneRowPerSupportedMajorAndTheBuildTools()
    {
        var seventeen = _machine.AddJdk(17, "17.0.12");
        var mvn = _machine.AddTool("mvn", "Apache Maven 3.9.9");

        var found = await UseCase().FindAsync(MachineChoice.None, null, TestContext.Current.CancellationToken);

        Assert.Equal([8, 11, 17, 21], found.Jdks.Select(j => j.Major));
        Assert.Equal(("17.0.12", seventeen), (found.Jdks[2].Version, found.Jdks[2].Home));
        Assert.All(found.Jdks.Where(j => j.Major != 17), j => Assert.Null(j.Home));
        Assert.Equal(mvn, found.Maven);
        Assert.Null(found.Gradle);
        Assert.Empty(found.Broken);
    }

    [Fact]
    public async Task AChoiceIsUsedOnTopOfTheConfigurationAndABrokenHomeIsReported()
    {
        var eleven = ManualJdk("jdk-11", "11.0.24");
        var broken = Directory.CreateDirectory(Path.Combine(_machine.Root, "manual", "empty")).FullName;
        var choice = new MachineChoice(new Dictionary<string, string> { ["11"] = eleven, ["21"] = broken }, null, null);

        var found = await UseCase().FindAsync(choice, null, TestContext.Current.CancellationToken);

        Assert.Equal(("11.0.24", eleven), (found.Jdks[1].Version, found.Jdks[1].Home));
        Assert.Null(found.Jdks[3].Home);
        Assert.Equal([broken], found.Broken);
        Assert.Empty(_machine.Config.Load(null, NoOverrides).Config.Java.JdkHomes);
    }

    [Fact]
    public void SaveWritesOnlyThePathsTheUserSet()
    {
        var eleven = ManualJdk("jdk-11", "11.0.24");
        var mvn = _machine.AddTool("mvn", "Apache Maven 3.9.9");

        UseCase().Save(new MachineChoice(new Dictionary<string, string> { ["11"] = eleven }, mvn, null), null);

        var java = _machine.Config.Load(null, NoOverrides).Config.Java;
        Assert.Equal(eleven, java.JdkHomes["11"]);
        Assert.Equal(mvn, java.MavenPath);
        Assert.Null(java.GradlePath);
        Assert.True(File.Exists(_machine.Home.ConfigFile));
    }

    [Fact]
    public void SavingNothingWritesNoFile()
    {
        UseCase().Save(MachineChoice.None, null);

        Assert.False(File.Exists(_machine.Home.ConfigFile));
    }
}
