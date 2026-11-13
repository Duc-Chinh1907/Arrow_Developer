using System.Diagnostics;
using Arrow.Common.Errors;
using Arrow.DataAccess.JavaHelper;
using Arrow.IntegrationTests.Fixtures;

namespace Arrow.IntegrationTests.JavaHelper;

/// <summary>The process rules of <c>JavaHelperClient</c> (P10 §5) and the C# tests listed in P10 §7.</summary>
[Trait("Category", "Integration")]
public class JavaHelperProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThousandCommandsRunOnOneProcess()
    {
        await using var client = new JavaHelperClient(JavaHelperEnvironment.Options());
        await client.PingAsync(Ct);
        var pid = client.ProcessId;
        for (var i = 0; i < 1000; i++)
        {
            Assert.True((await client.ParseAsync($"class C{i} {{}}", Ct)).Parsed);
        }

        Assert.Equal(pid, client.ProcessId);
    }

    [Fact]
    public async Task TimeoutRestartsOnceThenReportsJhTimeoutAndTheClientRecovers()
    {
        // A zero ListClassesTimeout cancels at once, so both attempts time out on any machine (a 1 ms timeout was flaky:
        // a fast runner answered within the ~15 ms timer resolution of Windows). Other commands keep their own timeout.
        var options = JavaHelperEnvironment.Options() with { ListClassesTimeout = TimeSpan.Zero };
        await using var client = new JavaHelperClient(options);
        await client.PingAsync(Ct);

        var error = await Assert.ThrowsAsync<ArrowException>(() => client.ListClassesAsync([FixtureRepo.FixturesDir], 1000, Ct));

        Assert.Equal(ErrorCodes.JhTimeout, error.Code);
        Assert.Equal(JavaHelperJar.Version, await client.PingAsync(Ct));
    }

    [Fact]
    public async Task WrongVersionIsJhVersionMismatch()
    {
        await using var client = new JavaHelperClient(JavaHelperEnvironment.Options() with { ExpectedVersion = "0.0.0" });
        var error = await Assert.ThrowsAsync<ArrowException>(() => client.PingAsync(Ct));
        Assert.Equal(ErrorCodes.JhVersionMismatch, error.Code);
        Assert.Equal(("0.0.0", JavaHelperJar.Version), (error.Args["expected"], error.Args["actual"]));
        Assert.Null(client.ProcessId);
    }

    [Fact]
    public async Task MissingJavaIsJhStartFailed()
    {
        var options = JavaHelperEnvironment.Options() with { JavaExecutable = Path.Combine(Path.GetTempPath(), "no-such-java", "java") };
        await using var client = new JavaHelperClient(options);
        Assert.Equal(ErrorCodes.JhStartFailed, (await Assert.ThrowsAsync<ArrowException>(() => client.PingAsync(Ct))).Code);
    }

    [Fact]
    public async Task CodeOverTwoMegabytesIsRefusedBeforeSending()
    {
        await using var client = new JavaHelperClient(JavaHelperEnvironment.Options());
        var error = await Assert.ThrowsAsync<ArrowException>(() => client.ParseAsync(new string('x', 2 * 1024 * 1024 + 1), Ct));
        Assert.Equal(ErrorCodes.JhInputTooLarge, error.Code);
        Assert.Null(client.ProcessId);
    }

    [Fact]
    public async Task TwoJobsGetTwoProcessesAndAThirdWaits()
    {
        await using var pool = new JavaHelperPool(JavaHelperEnvironment.Options(), size: 2);
        using var first = await pool.RentAsync(Ct);
        using var second = await pool.RentAsync(Ct);
        await Task.WhenAll(first.Helper.PingAsync(Ct), second.Helper.PingAsync(Ct));
        Assert.NotEqual(first.Client.ProcessId, second.Client.ProcessId);

        var third = pool.RentAsync(Ct);
        await Task.Delay(200, Ct);
        Assert.False(third.IsCompleted);
        first.Dispose();
        using var reused = await third;
        Assert.Same(first.Client, reused.Client);
        Assert.Equal(2, pool.Created);
    }

    [Fact]
    public async Task DisposeLeavesNoJavaProcess()
    {
        var client = new JavaHelperClient(JavaHelperEnvironment.Options());
        await client.PingAsync(Ct);
        var pid = client.ProcessId!.Value;
        using var process = Process.GetProcessById(pid);

        await client.DisposeAsync();

        Assert.True(process.WaitForExit(5_000));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.PingAsync(Ct));
    }

    /// <summary>P10 acceptance: list_classes and parse run on every .java file of the 9 fixtures without error.</summary>
    [Fact]
    public async Task ListClassesAndParseEveryFixtureFile()
    {
        await using var client = new JavaHelperClient(JavaHelperEnvironment.Options());
        var files = Directory.GetFiles(FixtureRepo.FixturesDir, "*.java", SearchOption.AllDirectories);
        var fixtures = Directory.GetDirectories(FixtureRepo.FixturesDir);
        Assert.Equal(9, fixtures.Length);
        Assert.All(fixtures, f => Assert.NotEmpty(Directory.GetFiles(f, "*.java", SearchOption.AllDirectories)));

        var listed = await client.ListClassesAsync([FixtureRepo.FixturesDir], 20_000, Ct);
        Assert.Empty(listed.Errors);
        Assert.False(listed.Truncated);
        Assert.Equal(files.Length, listed.Classes.Select(c => c.Path).Distinct().Count());
        Assert.Contains(listed.Classes, c => c.Fqn == "com.acme.billing.PriceCalculator");

        foreach (var file in files)
        {
            var parsed = await client.ParseAsync(await File.ReadAllTextAsync(file, Ct), Ct);
            Assert.True(parsed.Parsed, $"{file}: {parsed.Error}");
        }
    }
}
