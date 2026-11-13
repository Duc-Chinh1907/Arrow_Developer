using System.Diagnostics;
using System.Text;
using Arrow.DataAccess.Processes;

namespace Arrow.DataAccess.Tests.Processes;

/// <summary>
/// External commands (P00 §5). Uses only programs of the operating system, so no JDK is needed.
/// Parity with <c>ARROW_Framework/tests/Arrow.IntegrationTests/JavaHelperTests.cs</c> (<c>ProcessRunnerTests</c>):
/// the old capture test ran <c>java -version</c>; here a shell script plays that part.
/// </summary>
public class ProcessRunnerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Parity: <c>ProcessRunnerTests.CapturesOutputExitCodeAndEnvironment</c>.</summary>
    [Fact]
    public async Task CapturesOutputExitCodeAndEnvironment()
    {
        using var temp = new TempDir();
        Environment.SetEnvironmentVariable("ARROW_TEST_REMOVED", "still-here");
        var spec = Script(temp, OperatingSystem.IsWindows()
                ? "@echo off\r\necho value=%ARROW_TEST_VALUE%\r\necho removed=%ARROW_TEST_REMOVED%\r\necho to-stderr 1>&2\r\nexit /b 3\r\n"
                : "echo \"value=$ARROW_TEST_VALUE\"\necho \"removed=$ARROW_TEST_REMOVED\"\necho to-stderr 1>&2\nexit 3\n")
            with { Environment = new Dictionary<string, string?> { ["ARROW_TEST_VALUE"] = "hello-arrow", ["ARROW_TEST_REMOVED"] = null } };

        var result = await new ProcessRunner().RunAsync(spec, None);

        // ASCII only: cmd.exe prints in the console code page, not UTF-8. Java and Maven get UTF-8 through
        // JAVA_TOOL_OPTIONS, which block 1 sets (GĐ2).
        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("value=hello-arrow", result.Output, StringComparison.Ordinal);
        Assert.Contains("removed=\n", result.Output.Replace("removed= \n", "removed=\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("to-stderr", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", result.Output, StringComparison.Ordinal);
        Assert.Equal("to-stderr", result.Tail(1).Trim());
    }

    /// <summary>Parity: <c>ProcessRunnerTests.TimeoutKillsProcess</c>.</summary>
    [Fact]
    public async Task TimeoutKillsProcess()
    {
        var spec = Sleep() with { Timeout = TimeSpan.FromSeconds(1) };
        var watch = Stopwatch.StartNew();
        var result = await new ProcessRunner().RunAsync(spec, None);
        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    /// <summary>Parity: <c>ProcessRunnerTests.CancellationKillsProcessAndThrows</c>.</summary>
    [Fact]
    public async Task CancellationKillsProcessAndThrows()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(Sleep(), cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task LogFileHasCommandFirstAndExitLastAndIsRedacted()
    {
        using var temp = new TempDir();
        var log = temp.PathOf("logs", "compile.log");
        var spec = Script(temp, OperatingSystem.IsWindows() ? "@echo off\r\necho key sk-test-SECRET-123-abcdef\r\n" : "echo key sk-test-SECRET-123-abcdef\n")
            with { LogFile = log, Environment = new Dictionary<string, string?> { ["JAVA_HOME"] = "/jdk/17" } };

        await new ProcessRunner().RunAsync(spec, None);

        var lines = File.ReadAllText(log).TrimEnd('\n').Split('\n');
        Assert.StartsWith("$ ", lines[0], StringComparison.Ordinal);
        Assert.Contains("dir=" + temp.Root, lines[0], StringComparison.Ordinal);
        Assert.Contains("jdk=/jdk/17", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^# exit=0 seconds=\d+\.\d{3} timeout=no$", lines[^1]);
        Assert.DoesNotContain("SECRET", File.ReadAllText(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimedOutRunIsLoggedAsKilled()
    {
        using var temp = new TempDir();
        var log = temp.PathOf("sleep.log");
        await new ProcessRunner().RunAsync(Sleep() with { Timeout = TimeSpan.FromSeconds(1), LogFile = log }, None);
        Assert.Contains("# exit=killed", File.ReadAllText(log), StringComparison.Ordinal);
        Assert.EndsWith("timeout=yes\n", File.ReadAllText(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputOverFiveMegabytesKeepsTwoMegabytesAtEachEnd()
    {
        using var temp = new TempDir();
        var big = new StringBuilder();
        for (var i = 0; i < 70_000; i++)
        {
            big.Append("line ").Append(i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append('x', 80).Append('\n');
        }

        var data = temp.Write("big.txt", big.ToString());
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", "type", data], temp.Root)
            : new ProcessSpec("/bin/cat", [data], temp.Root);

        var result = await new ProcessRunner().RunAsync(spec, None);

        Assert.True(result.OutputTruncated);
        Assert.StartsWith("line 000000 ", result.Output, StringComparison.Ordinal);
        Assert.Contains("bytes omitted", result.Output, StringComparison.Ordinal);
        Assert.Contains("line 069999 ", result.Output, StringComparison.Ordinal);
        var size = Encoding.UTF8.GetByteCount(result.Output);
        Assert.InRange(size, 4 * 1024 * 1024 - 1000, 4 * 1024 * 1024 + 1000);
    }

    [Fact]
    public async Task SmallOutputIsKeptWhole()
    {
        using var temp = new TempDir();
        var result = await new ProcessRunner().RunAsync(
            Script(temp, OperatingSystem.IsWindows() ? "@echo off\r\necho one\r\necho two\r\n" : "echo one\necho two\n"), None);
        Assert.True(result.Succeeded);
        Assert.False(result.OutputTruncated);
        Assert.Equal("one\ntwo\n", result.Output);
    }

    [Fact]
    public async Task MissingProgramThrows()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => new ProcessRunner().RunAsync(
            new ProcessSpec("arrow-no-such-program-xyz", [], Path.GetTempPath()), None));
    }

    private static ProcessSpec Sleep() => OperatingSystem.IsWindows()
        ? new ProcessSpec("ping", ["-n", "30", "127.0.0.1"], Path.GetTempPath())
        : new ProcessSpec("/bin/sleep", ["30"], Path.GetTempPath());

    private static ProcessSpec Script(TempDir temp, string body)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessSpec(temp.Write("script.cmd", body), [], temp.Root);
        }

        return new ProcessSpec("/bin/sh", [temp.Write("script.sh", body)], temp.Root);
    }
}
