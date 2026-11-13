using System.Security.Cryptography;
using System.Text.Json;
using Arrow.Common.Errors;
using Arrow.Common.Json;
using Arrow.DataAccess.IO;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.Tools;

/// <summary>An entry of <c>tools.lock.json</c> (P09 §6.1).</summary>
/// <param name="Id">Tool id, e.g. <c>jacoco-agent</c>.</param>
/// <param name="File">Path below <c>ARROW_HOME/tools</c>, with <c>/</c>.</param>
/// <param name="Maven">Maven coordinates <c>group:artifact:version[:type[:classifier]]</c>; null for a URL or embedded tool.</param>
/// <param name="Url">Direct download URL; null for a Maven or embedded tool.</param>
/// <param name="Embedded">Shipped inside ARROW (java-helper); never downloaded.</param>
/// <param name="Sha256">Expected sha256 in lower-case hex.</param>
public sealed record ToolEntry(string Id, string File, string? Maven, string? Url, bool Embedded, string? Sha256);

/// <summary>The content of <c>tools.lock.json</c>.</summary>
public sealed record ToolLock(IReadOnlyList<ToolEntry> Tools)
{
    /// <summary>The lock file embedded in this build.</summary>
    public static ToolLock Embedded { get; } = Load();

    /// <summary>The entry of <paramref name="id"/>; <c>CFG_TOOL_MISSING</c> when unknown.</summary>
    public ToolEntry Get(string id) =>
        Tools.FirstOrDefault(t => t.Id == id) ?? throw new ArrowException(ErrorCodes.CfgToolMissing, $"Không có công cụ tên {id} trong tools.lock.json.");

    private static ToolLock Load()
    {
        using var stream = typeof(ToolLock).Assembly.GetManifestResourceStream("Arrow.DataAccess.Tools.tools.lock.json")
            ?? throw new InvalidOperationException("tools.lock.json is not embedded.");
        return ArrowJson.Deserialize<ToolLock>(new StreamReader(stream).ReadToEnd());
    }
}

/// <summary>A line of <c>tools/tools.installed.json</c> (P09 §6.2).</summary>
public sealed record InstalledTool(string Id, string File, string Sha256, DateTimeOffset InstalledAt);

/// <summary>Whether a tool is ready.</summary>
/// <param name="Entry">The lock entry.</param>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="Installed">The file exists and has the locked sha256.</param>
/// <param name="Problem">Why it is not ready (missing, wrong sha256); null when installed.</param>
public sealed record ToolState(ToolEntry Entry, string Path, bool Installed, string? Problem);

/// <summary>The external tools of block 4 (P09 §6).</summary>
public interface IToolStore
{
    /// <summary>Every tool of the lock file but the embedded ones, with its state.</summary>
    IReadOnlyList<ToolState> List();

    /// <summary>
    /// The path of tool <paramref name="id"/>, downloading it first when it is missing or damaged. Raises <c>CFG_TOOL_MISSING</c>
    /// when it cannot be downloaded and <c>CFG_TOOL_CHECKSUM</c> when the download has another sha256.
    /// </summary>
    Task<string> EnsureAsync(string id, IProgress<string>? progress, CancellationToken ct);

    /// <summary>
    /// A jar from Maven Central that is not in the lock file (P05 §5: <c>junit-platform-launcher</c> of the module's version),
    /// kept in <c>tools/&lt;folder&gt;/</c> and checked against the <c>.sha1</c> file Maven Central publishes with it.
    /// </summary>
    Task<string> EnsureMavenArtifactAsync(string group, string artifact, string version, string folder, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// <see cref="IToolStore"/>: downloads over HTTPS from Maven Central (or <c>ARROW_MAVEN_MIRROR</c>) and GitHub, without Maven on the
/// machine. A file is written to <c>.part</c>, checked, then renamed; a wrong sha256 deletes it. HTTP 429, 5xx and network errors
/// are tried again after <see cref="RetryDelays"/> (or <c>Retry-After</c>, at most 30 s).
/// </summary>
public sealed class ToolStore(ArrowHome home, HttpClient http, Func<string, string?> getEnvironment, ToolLock? lockFile = null, TimeProvider? time = null) : IToolStore
{
    /// <summary>Waits before the retries of a download.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>Longest <c>Retry-After</c> honoured.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Variable that replaces <see cref="MavenCentral"/> (P09 §6.2).</summary>
    public const string MirrorVariable = "ARROW_MAVEN_MIRROR";

    /// <summary>Maven Central.</summary>
    public const string MavenCentral = "https://repo1.maven.org/maven2";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly ToolLock _lock = lockFile ?? ToolLock.Embedded;

    private string InstalledFile => Path.Combine(home.ToolsDir, "tools.installed.json");

    /// <inheritdoc />
    public IReadOnlyList<ToolState> List() => _lock.Tools.Where(t => !t.Embedded).Select(State).ToList();

    private ToolState State(ToolEntry entry)
    {
        var path = PathOf(entry.File);
        if (!File.Exists(path))
        {
            return new ToolState(entry, path, false, "chưa tải");
        }

        return entry.Sha256 is null || Sha256Of(path) == entry.Sha256
            ? new ToolState(entry, path, true, null)
            : new ToolState(entry, path, false, "sai sha256");
    }

    /// <inheritdoc />
    public async Task<string> EnsureAsync(string id, IProgress<string>? progress, CancellationToken ct)
    {
        var entry = _lock.Get(id);
        if (entry.Embedded)
        {
            throw new ArrowException(ErrorCodes.CfgToolMissing, $"{id} được nhúng sẵn, không tải.");
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = State(entry);
            if (state.Installed)
            {
                return state.Path;
            }

            var url = DownloadUrl(entry);
            progress?.Report($"Tải {id} từ {url.Host}");
            var path = state.Path;
            var part = path + ".part";
            await DownloadAsync(id, url, part, ct).ConfigureAwait(false);
            var sha = Sha256Of(part);
            if (entry.Sha256 is not null && sha != entry.Sha256)
            {
                File.Delete(part);
                throw new ArrowException(ErrorCodes.CfgToolChecksum, $"{id}: sha256 {sha} khác {entry.Sha256}");
            }

            File.Move(part, path, overwrite: true);
            RecordInstalled(new InstalledTool(id, entry.File, sha, DateTimeOffset.UtcNow));
            return path;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> EnsureMavenArtifactAsync(string group, string artifact, string version, string folder, IProgress<string>? progress, CancellationToken ct)
    {
        var relative = $"{folder}/{artifact}-{version}.jar";
        var path = PathOf(relative);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            var url = new Uri($"{MavenBase()}/{MavenPath(group, artifact, version, "jar", null)}");
            progress?.Report($"Tải {artifact} {version} từ {url.Host}");
            var expected = await ReadSha1Async(artifact, new Uri(url + ".sha1"), ct).ConfigureAwait(false);
            var part = path + ".part";
            await DownloadAsync(artifact, url, part, ct).ConfigureAwait(false);
            string actual;
            await using (var stream = File.OpenRead(part))
            {
                actual = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false));
            }

            if (actual != expected)
            {
                File.Delete(part);
                throw new ArrowException(ErrorCodes.CfgToolChecksum, $"{artifact} {version}: sha1 {actual} khác {expected}");
            }

            File.Move(part, path, overwrite: true);
            RecordInstalled(new InstalledTool($"{group}:{artifact}:{version}", relative, Sha256Of(path), DateTimeOffset.UtcNow));
            return path;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Where <paramref name="entry"/> is downloaded from.</summary>
    public Uri DownloadUrl(ToolEntry entry)
    {
        if (entry.Url is { } url)
        {
            return new Uri(url);
        }

        var parts = (entry.Maven ?? throw new ArrowException(ErrorCodes.CfgToolMissing, $"{entry.Id} không có nguồn tải.")).Split(':');
        if (parts.Length < 3)
        {
            throw new ArrowException(ErrorCodes.CfgToolMissing, $"{entry.Id}: toạ độ Maven sai ({entry.Maven}).");
        }

        var type = parts.Length > 3 ? parts[3] : "jar";
        var classifier = parts.Length > 4 ? parts[4] : null;
        return new Uri($"{MavenBase()}/{MavenPath(parts[0], parts[1], parts[2], type, classifier)}");
    }

    /// <summary><c>group/path/artifact/version/artifact-version[-classifier].type</c>.</summary>
    public static string MavenPath(string group, string artifact, string version, string type, string? classifier) =>
        $"{group.Replace('.', '/')}/{artifact}/{version}/{artifact}-{version}{(classifier is null ? "" : "-" + classifier)}.{type}";

    private string MavenBase() => (getEnvironment(MirrorVariable) is { Length: > 0 } mirror ? mirror : MavenCentral).TrimEnd('/');

    private string PathOf(string file) => Path.Combine(home.ToolsDir, file.Replace('/', Path.DirectorySeparatorChar));

    private async Task DownloadAsync(string id, Uri url, string part, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? wait = null;
            string reason;
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                    return;
                }

                var status = (int)response.StatusCode;
                reason = $"{url} trả HTTP {status}";
                if (status != 429 && status < 500)
                {
                    TryDelete(part);
                    throw new ArrowException(ErrorCodes.CfgToolMissing, $"{id}: {reason}");
                }

                wait = response.Headers.RetryAfter?.Delta;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                reason = $"không tải được {url} ({ex.Message})";
            }

            TryDelete(part);
            if (attempt >= RetryDelays.Count)
            {
                throw new ArrowException(ErrorCodes.CfgToolMissing, $"{id}: {reason}");
            }

            var delay = wait is { } after && after > TimeSpan.Zero ? (after < MaxRetryAfter ? after : MaxRetryAfter) : RetryDelays[attempt];
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> ReadSha1Async(string id, Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ArrowException(ErrorCodes.CfgToolMissing, $"{id}: {url} trả HTTP {(int)response.StatusCode}");
            }

            // The file holds the hex digest, sometimes followed by the file name.
            var text = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            return text.Split(' ', '\t', '\n')[0].ToLowerInvariant();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ArrowException(ErrorCodes.CfgToolMissing, $"{id}: không tải được {url} ({ex.Message})");
        }
    }

    private void RecordInstalled(InstalledTool tool)
    {
        var list = File.Exists(InstalledFile)
            ? ReadInstalled().Where(t => t.Id != tool.Id).ToList()
            : [];
        list.Add(tool);
        AtomicFile.WriteAllText(InstalledFile, ArrowJson.Serialize(list.OrderBy(t => t.Id, StringComparer.Ordinal).ToList()));
    }

    /// <summary>The tools recorded as installed.</summary>
    public IReadOnlyList<InstalledTool> ReadInstalled()
    {
        try
        {
            return File.Exists(InstalledFile) ? ArrowJson.Deserialize<List<InstalledTool>>(File.ReadAllText(InstalledFile)) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>sha256 of a file in lower-case hex.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
