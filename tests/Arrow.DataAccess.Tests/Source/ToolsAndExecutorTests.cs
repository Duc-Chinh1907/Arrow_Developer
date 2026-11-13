using Arrow.Common.Analysis;
using Arrow.DataAccess.Build;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Tools;

namespace Arrow.DataAccess.Tests.Source;

/// <summary>Finding programs, the build environment and working copies (P01 §4.2, §6, P07 §7, P08 §2.6).</summary>
public class ToolsAndExecutorTests
{
    [Fact]
    public void FindsProgramsOnPathWithPathext()
    {
        using var temp = new TempDir();
        var first = Path.Combine(temp.Root, "first");
        var second = Path.Combine(temp.Root, "second");
        temp.Write("second/mvn.cmd", "@echo off");
        temp.Write("second/mvn", "#!/bin/sh");
        Directory.CreateDirectory(first);
        var env = new Dictionary<string, string?> { ["PATH"] = first + Path.PathSeparator + "\"" + second + "\"", ["PATHEXT"] = ".EXE;.CMD" };

        Assert.Equal(Path.Combine(second, "mvn.cmd"), new ToolLocator(n => env.GetValueOrDefault(n), isWindows: true).FindOnPath("mvn"));
        Assert.Equal(Path.Combine(second, "mvn"), new ToolLocator(n => env.GetValueOrDefault(n), isWindows: false).FindOnPath("mvn"));
        Assert.Null(new ToolLocator(n => env.GetValueOrDefault(n), isWindows: false).FindOnPath("gradle"));
    }

    [Fact]
    public void ConfiguredPathWinsAndMayBeAFolder()
    {
        using var temp = new TempDir();
        temp.Write("maven/bin/mvn.cmd", "@echo off");
        var locator = new ToolLocator(_ => null, isWindows: true);
        Assert.Equal(Path.Combine(temp.Root, "maven", "bin", "mvn.cmd"), locator.Resolve(Path.Combine(temp.Root, "maven"), "mvn"));
        Assert.Equal(Path.Combine(temp.Root, "maven", "bin", "mvn.cmd"), locator.Resolve(Path.Combine(temp.Root, "maven", "bin", "mvn.cmd"), "mvn"));
        Assert.Null(locator.Resolve(Path.Combine(temp.Root, "nothing"), "mvn"));
        Assert.Equal("mvnw.cmd", locator.WrapperName(gradle: false));
        Assert.Equal("gradlew", new ToolLocator(_ => null, isWindows: false).WrapperName(gradle: true));
    }

    [Fact]
    public void JdkByMajorKeepsTheFirstFound()
    {
        var map = JdkLocator.ByMajor([new(17, "17.0.12", "/config/17", "config"), new(17, "17.0.1", "/path/17", "PATH"), new(8, "1.8.0_422", "/x/8", "folder"), new(0, "", "/broken", "config")]);
        Assert.Equal([8, 17], map.Keys);
        Assert.Equal("/config/17", map[17].Home);
    }

    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessSpec? Last { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Last = spec;
            return Task.FromResult(result);
        }
    }

    private static readonly BuildSetup Setup = new(BuildToolKind.Maven, "mvnw", true, "core", BuildMode.Standalone, null, ["-B"], null);

    [Fact]
    public async Task BuildEnvironmentUsesTheChosenJdkAndKeepsUserMemoryOptions()
    {
        var runner = new FakeRunner(new ProcessResult(0, false, "BUILD SUCCESS", TimeSpan.FromSeconds(1)));
        var env = new Dictionary<string, string?> { ["PATH"] = "/usr/bin", ["GRADLE_OPTS"] = "-Xmx3g" };
        var executor = new BuildExecutor(runner, n => env.GetValueOrDefault(n), []);
        var project = Path.Combine(Path.GetTempPath(), "p1");
        var result = await executor.RunAsync(Setup, project, ["-DskipTests", "test-compile"], "/jdk/17", TimeSpan.FromMinutes(1), null, TestContext.Current.CancellationToken);

        Assert.Equal(BuildFailureKind.None, result.Failure);
        Assert.Equal(Path.Combine(project, "mvnw"), runner.Last!.FileName);
        Assert.Equal(Path.Combine(project, "core"), runner.Last.WorkingDirectory);
        Assert.Equal(["-f", "pom.xml", "-B", "-DskipTests", "test-compile"], runner.Last.Arguments);
        var vars = runner.Last.Environment!;
        Assert.Equal("/jdk/17", vars["JAVA_HOME"]);
        Assert.StartsWith(Path.Combine("/jdk/17", "bin") + Path.PathSeparator, vars["PATH"], StringComparison.Ordinal);
        Assert.Equal(BuildCommands.JavaToolOptions, vars["JAVA_TOOL_OPTIONS"]);
        Assert.Equal("-Xmx1g", vars["MAVEN_OPTS"]);
        Assert.Equal("-Xmx3g", vars["GRADLE_OPTS"]);
        Assert.Null(vars["MAVEN_ARGS"]);
    }

    [Fact]
    public async Task CorruptCachedJarTurnsAFailureIntoARetry()
    {
        using var temp = new TempDir();
        var jar = temp.Write("m2/g/x-1.jar", "<html>");
        var runner = new FakeRunner(new ProcessResult(1, false, $"[ERROR] error reading {jar}; zip END header not found", TimeSpan.Zero));
        var executor = new BuildExecutor(runner, _ => null, [Path.Combine(temp.Root, "m2")]);
        var result = await executor.RunAsync(Setup, temp.Root, ["test"], "/jdk", TimeSpan.FromMinutes(1), null, TestContext.Current.CancellationToken);
        Assert.Equal((BuildFailureKind.Network, 1), (result.Failure, result.RemovedCorruptJars));
        Assert.False(File.Exists(jar));
    }

    [Fact]
    public async Task WorkspaceCloneAndDeleteHandleReadOnlyFiles()
    {
        using var temp = new TempDir();
        temp.Write("src/a.txt", "a");
        var readOnly = temp.Write("src/.git/objects/ab/cd", "object");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var wrapper = temp.Write("src/mvnw", "#!/bin/sh");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var workspaces = new WorkspaceManager();
        var copy = await workspaces.CloneAsync(Path.Combine(temp.Root, "src"), Path.Combine(temp.Root, "copy"), TestContext.Current.CancellationToken);
        Assert.Equal("a", File.ReadAllText(Path.Combine(copy, "a.txt")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(copy, "mvnw")).HasFlag(UnixFileMode.UserExecute));
        }

        await workspaces.DeleteAsync(Path.Combine(temp.Root, "src"), TestContext.Current.CancellationToken);
        await workspaces.DeleteAsync(copy, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(copy));
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "src")));
        await workspaces.DeleteAsync(Path.Combine(temp.Root, "missing"), TestContext.Current.CancellationToken);
    }
}
