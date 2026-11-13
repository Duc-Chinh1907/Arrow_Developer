using Arrow.Common.Errors;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.Tests.Storage;

/// <summary>Where <c>ARROW_HOME</c> is and what it may look like (P09 §2).</summary>
public class ArrowHomeTests
{
    private static readonly string Home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "home"));

    [Fact]
    public void EnvironmentVariableWins()
    {
        var target = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "custom-arrow"));
        var env = Env(("ARROW_HOME", target), ("LOCALAPPDATA", Path.Combine(Home, "local")));
        Assert.Equal(target, ArrowHome.Resolve(env, isWindows: true, userProfile: Home).Root);
    }

    [Fact]
    public void WindowsUsesLocalAppData()
    {
        var local = Path.GetFullPath(Path.Combine(Home, "AppData", "Local"));
        Assert.Equal(Path.Combine(local, "arrow"), ArrowHome.Resolve(Env(("LOCALAPPDATA", local)), isWindows: true, userProfile: Home).Root);
    }

    [Fact]
    public void UnixUsesXdgDataHomeThenDotArrow()
    {
        var xdg = Path.GetFullPath(Path.Combine(Home, ".local", "share"));
        Assert.Equal(Path.Combine(xdg, "arrow"), ArrowHome.Resolve(Env(("XDG_DATA_HOME", xdg)), isWindows: false, userProfile: Home).Root);
        Assert.Equal(Path.Combine(Home, ".arrow"), ArrowHome.Resolve(Env(), isWindows: false, userProfile: Home).Root);
    }

    [Theory]
    [InlineData("C:/Users/Nguyen Van A/AppData/Local/arrow", false)]
    [InlineData("C:/Users/Chính/AppData/Local/arrow", false)]
    [InlineData("D:/arrow", true)]
    public void PathMustBeAsciiWithoutSpaces(string path, bool usable)
    {
        Assert.Equal(usable, ArrowHome.IsUsablePath(path));
    }

    [Fact]
    public void ValidateReportsCfgHomePath()
    {
        var bad = new ArrowHome(Path.Combine(Path.GetTempPath(), "có dấu cách"));
        var error = bad.Validate();
        Assert.Equal(ErrorCodes.CfgHomePath, error!.Code);
        Assert.Equal(bad.Root, error.Args["path"]);
        Assert.Null(new ArrowHome(Path.Combine(Path.GetTempPath(), "arrow-ok")).Validate());
    }

    [Fact]
    public void LayoutFollowsP09()
    {
        var home = new ArrowHome(Path.Combine(Path.GetTempPath(), "arrow-layout"));
        Assert.Equal(Path.Combine(home.Root, "config.json"), home.ConfigFile);
        Assert.Equal(Path.Combine(home.Root, "runs"), home.RunsDir);
        Assert.Equal(Path.Combine(home.Root, "history.jsonl"), home.HistoryFile);
        Assert.Equal(Path.Combine(home.Root, "secrets"), home.SecretsDir);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;
}
