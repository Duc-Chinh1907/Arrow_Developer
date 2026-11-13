using System.Net;
using System.Security.Cryptography;
using System.Text;
using Arrow.Common.Errors;
using Arrow.DataAccess.Storage;
using Arrow.DataAccess.Tools;

namespace Arrow.DataAccess.Tests.Tools;

/// <summary>Download, checksum and mirror rules of P09 §6, with a fake HTTP server.</summary>
public sealed class ToolStoreTests : IDisposable
{
    private static readonly byte[] Jar = Encoding.ASCII.GetBytes("PK fake jar content");
    private static readonly string JarSha256 = Convert.ToHexStringLower(SHA256.HashData(Jar));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-tools", Guid.NewGuid().ToString("N")[..8]);
    private readonly ArrowHome _home;

    public ToolStoreTests()
    {
        _home = new ArrowHome(_root);
        _home.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Serves bytes by URL; anything else is 404.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = [];

        public Exception? Failure { get; set; }

        /// <summary>Statuses answered first, before the files are served.</summary>
        public Queue<HttpStatusCode> Busy { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            if (Failure is not null)
            {
                return Task.FromException<HttpResponseMessage>(Failure);
            }

            if (Busy.TryDequeue(out var status))
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            return Task.FromResult(Files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static ToolLock Lock(string? sha256 = null) => new(
    [
        new ToolEntry("jacoco-agent", "jacoco-0.8.12/jacocoagent.jar", "org.jacoco:org.jacoco.agent:0.8.12:jar:runtime", null, false, sha256 ?? JarSha256),
        new ToolEntry("tsdetect", "tsdetect-2.2/TestSmellDetector.jar", null, "https://github.com/TestSmells/TestSmellDetector/releases/download/v2.2/TestSmellDetector.jar", false, JarSha256),
        new ToolEntry("java-helper", "java-helper-1.1.0.jar", null, null, true, null),
    ]);

    private const string AgentUrl = "https://repo1.maven.org/maven2/org/jacoco/org.jacoco.agent/0.8.12/org.jacoco.agent-0.8.12-runtime.jar";

    private readonly ImmediateTime _time = new();

    private ToolStore Store(FakeServer server, ToolLock? lockFile = null, string? mirror = null) =>
        new(_home, new HttpClient(server), name => name == ToolStore.MirrorVariable ? mirror : null, lockFile ?? Lock(), _time);

    /// <summary>Fires every timer at once and records the waits.</summary>
    private sealed class ImmediateTime : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Delays.Add(dueTime);
                callback(state);
            }

            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Maven Central answers 429 when it is busy: the download waits and tries again.
    [Fact]
    public async Task BusyServersAreTriedAgain()
    {
        var server = new FakeServer { Files = { [AgentUrl] = Jar } };
        server.Busy.Enqueue(HttpStatusCode.TooManyRequests);
        server.Busy.Enqueue(HttpStatusCode.BadGateway);
        var path = await Store(server).EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));
        Assert.Equal(3, server.Requested.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _time.Delays);
    }

    [Fact]
    public async Task DownloadsChecksAndRecordsATool()
    {
        var server = new FakeServer { Files = { [AgentUrl] = Jar } };
        var store = Store(server);
        Assert.False(Assert.Single(store.List(), s => s.Entry.Id == "jacoco-agent").Installed);

        var path = await store.EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(_root, "tools", "jacoco-0.8.12", "jacocoagent.jar"), path);
        Assert.Equal(Jar, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal(("jacoco-agent", JarSha256), (store.ReadInstalled().Single().Id, store.ReadInstalled().Single().Sha256));
        Assert.True(store.List().Single(s => s.Entry.Id == "jacoco-agent").Installed);

        // Installed tools are not downloaded again.
        await store.EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken);
        Assert.Single(server.Requested);
    }

    [Fact]
    public async Task AWrongChecksumDeletesTheFile()
    {
        var store = Store(new FakeServer { Files = { [AgentUrl] = Jar } }, Lock(sha256: new string('0', 64)));
        var error = await Assert.ThrowsAsync<ArrowException>(() => store.EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.CfgToolChecksum, error.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tools", "jacoco-0.8.12")));
    }

    [Fact]
    public async Task ADamagedFileIsDownloadedAgain()
    {
        var path = Path.Combine(_root, "tools", "jacoco-0.8.12", "jacocoagent.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "truncated", TestContext.Current.CancellationToken);
        var store = Store(new FakeServer { Files = { [AgentUrl] = Jar } });
        Assert.Equal("sai sha256", store.List().Single(s => s.Entry.Id == "jacoco-agent").Problem);
        await store.EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken);
        Assert.Equal(Jar, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheMirrorReplacesMavenCentralButNotDirectUrls()
    {
        var server = new FakeServer
        {
            Files =
            {
                ["https://nexus.example/repository/maven/org/jacoco/org.jacoco.agent/0.8.12/org.jacoco.agent-0.8.12-runtime.jar"] = Jar,
                ["https://github.com/TestSmells/TestSmellDetector/releases/download/v2.2/TestSmellDetector.jar"] = Jar,
            },
        };
        var store = Store(server, mirror: "https://nexus.example/repository/maven/");
        await store.EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken);
        await store.EnsureAsync("tsdetect", null, TestContext.Current.CancellationToken);
        Assert.Equal(2, server.Requested.Count);
        Assert.StartsWith("https://nexus.example/", server.Requested[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoNetworkIsToolMissing()
    {
        var store = Store(new FakeServer { Failure = new HttpRequestException("No such host is known.") });
        var error = await Assert.ThrowsAsync<ArrowException>(() => store.EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.CfgToolMissing, error.Code);
        Assert.Contains("No such host", error.Message, StringComparison.Ordinal);
        Assert.Equal(ToolStore.RetryDelays, _time.Delays); // tried 4 times

        var missing = await Assert.ThrowsAsync<ArrowException>(() => Store(new FakeServer()).EnsureAsync("jacoco-agent", null, TestContext.Current.CancellationToken));
        Assert.Contains("404", missing.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "tools", "jacoco-0.8.12")) && Directory.GetFiles(Path.Combine(_root, "tools", "jacoco-0.8.12")).Length > 0);
    }

    [Fact]
    public async Task UnknownAndEmbeddedToolsAreNotDownloaded()
    {
        var store = Store(new FakeServer());
        Assert.DoesNotContain(store.List(), s => s.Entry.Id == "java-helper");
        Assert.Equal(ErrorCodes.CfgToolMissing, (await Assert.ThrowsAsync<ArrowException>(() => store.EnsureAsync("evosuite", null, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(ErrorCodes.CfgToolMissing, (await Assert.ThrowsAsync<ArrowException>(() => store.EnsureAsync("java-helper", null, TestContext.Current.CancellationToken))).Code);
    }

    // P05 §5: junit-platform-launcher of the module's version, checked against the .sha1 of Maven Central.
    [Fact]
    public async Task MavenArtifactsOutsideTheLockAreCheckedWithTheirSha1()
    {
        const string url = "https://repo1.maven.org/maven2/org/junit/platform/junit-platform-launcher/1.10.2/junit-platform-launcher-1.10.2.jar";
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(Jar));
        var server = new FakeServer { Files = { [url] = Jar, [url + ".sha1"] = Encoding.ASCII.GetBytes(sha1 + "  junit-platform-launcher-1.10.2.jar\n") } };
        var path = await Store(server).EnsureMavenArtifactAsync("org.junit.platform", "junit-platform-launcher", "1.10.2", "junit-platform", null, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(_root, "tools", "junit-platform", "junit-platform-launcher-1.10.2.jar"), path);

        var bad = new FakeServer { Files = { [url] = Jar, [url + ".sha1"] = Encoding.ASCII.GetBytes(new string('a', 40)) } };
        var other = new ArrowHome(Path.Combine(_root, "other"));
        other.EnsureCreated();
        var error = await Assert.ThrowsAsync<ArrowException>(() => new ToolStore(other, new HttpClient(bad), _ => null, Lock())
            .EnsureMavenArtifactAsync("org.junit.platform", "junit-platform-launcher", "1.10.2", "junit-platform", null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.CfgToolChecksum, error.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "other", "tools", "junit-platform")));
    }

    [Fact]
    public void TheEmbeddedLockHasTheToolsOfP09WithoutEvoSuite()
    {
        var ids = ToolLock.Embedded.Tools.Select(t => t.Id).ToList();
        Assert.Equal(13, ids.Count);
        Assert.DoesNotContain(ids, id => id.Contains("evosuite", StringComparison.OrdinalIgnoreCase));
        Assert.All(ToolLock.Embedded.Tools.Where(t => !t.Embedded), t => Assert.Matches("^[0-9a-f]{64}$", t.Sha256!));
        Assert.Equal("org/jacoco/org.jacoco.cli/0.8.12/org.jacoco.cli-0.8.12-nodeps.jar", ToolStore.MavenPath("org.jacoco", "org.jacoco.cli", "0.8.12", "jar", "nodeps"));
    }
}
