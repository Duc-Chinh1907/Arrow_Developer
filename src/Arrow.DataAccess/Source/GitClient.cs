using System.Collections.Concurrent;
using Arrow.DataAccess.Processes;

namespace Arrow.DataAccess.Source;

/// <summary>Outcome of a git network command.</summary>
public enum GitStatus
{
    Ok,

    /// <summary>The server refused to send a commit by SHA; fetching everything may still find it.</summary>
    Refused,

    /// <summary>Network error or timeout: retry.</summary>
    Network,

    /// <summary>Login needed and git could not log in, or the repository does not exist (GitHub answers the same for both).</summary>
    AuthRequired,

    /// <summary>The ref or commit does not exist.</summary>
    Missing,
}

/// <summary>
/// git commands of block 1 (P01 §3.2). git never waits for a password (<c>GIT_TERMINAL_PROMPT=0</c>), uses the credential helper
/// already set up on the machine, and does not download LFS files. Commands that write the same mirror run one at a time.
/// </summary>
public sealed class GitClient(IProcessRunner runner, string git)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MirrorLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(2);

    // from ARROW_Framework/src/Arrow.Service/External/GitClient.cs (extended with the auth markers of P01 §3.2 step 6)
    private static readonly string[] RefusedMarkers =
    [
        "not our ref", "unadvertised object", "couldn't find remote ref", "no such remote ref", "did not send all necessary objects",
        "fatal: remote error",
    ];

    private static readonly string[] NetworkMarkers =
    [
        "could not resolve host", "connection timed out", "connection reset", "connection refused", "early eof", "rpc failed",
        "operation timed out", "the remote end hung up", "network is unreachable", "ssl_error", "gnutls",
        "http2 framing layer", "temporary failure", "failed to connect",
    ];

    private static readonly string[] AuthMarkers =
    [
        "repository not found", "could not read username", "could not read password", "authentication failed",
        "terminal prompts disabled", "returned error: 403", "returned error: 401", "permission denied",
    ];

    /// <summary>Program used, absolute path.</summary>
    public string Executable { get; } = git;

    /// <summary>Classifies a failed network command.</summary>
    public static GitStatus Classify(ProcessResult result)
    {
        if (result.Succeeded)
        {
            return GitStatus.Ok;
        }

        if (result.TimedOut)
        {
            return GitStatus.Network;
        }

        var text = result.Output.ToLowerInvariant();
        return RefusedMarkers.Any(text.Contains) ? GitStatus.Refused
            : AuthMarkers.Any(text.Contains) ? GitStatus.AuthRequired
            : NetworkMarkers.Any(text.Contains) || text.Contains("unable to access", StringComparison.Ordinal) ? GitStatus.Network
            : GitStatus.Missing;
    }

    /// <summary>Runs git with the fixed environment of P01 §3.2 step 7.</summary>
    public Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct, string? logFile = null) =>
        runner.RunAsync(new ProcessSpec(
            Executable,
            ["-c", "core.longpaths=true", "-c", "core.autocrlf=false", "-c", "advice.detachedHead=false", .. arguments],
            workingDirectory,
            new Dictionary<string, string?>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
                ["GIT_LFS_SKIP_SMUDGE"] = "1",
                ["LANG"] = "C",
                ["LC_ALL"] = "C",
            },
            timeout,
            LogFile: logFile), ct);

    /// <summary>Creates the bare mirror if needed and points <c>origin</c> at <paramref name="url"/>.</summary>
    public Task EnsureMirrorAsync(string mirror, string url, CancellationToken ct) => WithLock(mirror, async () =>
    {
        if (!File.Exists(Path.Combine(mirror, "HEAD")))
        {
            Directory.CreateDirectory(mirror);
            Check(await RunAsync(["init", "--bare", "-q", mirror], mirror, LocalTimeout, ct).ConfigureAwait(false), "git init --bare");
        }

        var current = await RunAsync(["-C", mirror, "remote", "get-url", "origin"], mirror, LocalTimeout, ct).ConfigureAwait(false);
        if (!current.Succeeded)
        {
            Check(await RunAsync(["-C", mirror, "remote", "add", "origin", url], mirror, LocalTimeout, ct).ConfigureAwait(false), "git remote add");
        }
        else if (current.Output.Trim() != url)
        {
            Check(await RunAsync(["-C", mirror, "remote", "set-url", "origin", url], mirror, LocalTimeout, ct).ConfigureAwait(false), "git remote set-url");
        }

        return true;
    }, ct);

    /// <summary><c>git ls-remote &lt;args&gt;</c> in the mirror.</summary>
    public Task<ProcessResult> LsRemoteAsync(string mirror, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(["-C", mirror, "ls-remote", .. arguments], mirror, LocalTimeout * 2, ct);

    /// <summary>
    /// <c>git ls-remote origin &lt;ref&gt; &lt;ref&gt;^{}</c>: with the pattern alone git does not print the <c>^{}</c> line that gives the
    /// commit of an annotated tag.
    /// </summary>
    public Task<ProcessResult> LsRemoteRefAsync(string mirror, string gitRef, CancellationToken ct) =>
        LsRemoteAsync(mirror, ["origin", gitRef, gitRef + "^{}"], ct);

    /// <summary>True when the mirror already has <paramref name="sha"/>.</summary>
    public async Task<bool> HasCommitAsync(string mirror, string sha, CancellationToken ct) =>
        (await RunAsync(["-C", mirror, "cat-file", "-e", sha + "^{commit}"], mirror, LocalTimeout, ct).ConfigureAwait(false)).Succeeded;

    /// <summary>
    /// Fetches <paramref name="sha"/> into the mirror (P01 §3.2 step 4): <c>fetch --depth 1 origin &lt;sha&gt;</c>; when the server refuses
    /// to send a commit by SHA, every branch and tag without a depth limit.
    /// </summary>
    public Task<(GitStatus Status, string Detail)> FetchAsync(string mirror, string sha, CancellationToken ct) => WithLock(mirror, async () =>
    {
        if (await HasCommitAsync(mirror, sha, ct).ConfigureAwait(false))
        {
            return (GitStatus.Ok, "already in the mirror");
        }

        var shallow = await RunAsync(["-C", mirror, "fetch", "-q", "--depth", "1", "origin", sha], mirror, NetworkTimeout, ct).ConfigureAwait(false);
        if (shallow.Succeeded && await HasCommitAsync(mirror, sha, ct).ConfigureAwait(false))
        {
            return (GitStatus.Ok, "fetch --depth 1");
        }

        var status = shallow.Succeeded ? GitStatus.Refused : Classify(shallow);
        if (status != GitStatus.Refused)
        {
            return (status, shallow.Tail(5));
        }

        List<string> full = ["-C", mirror, "fetch", "-q"];
        if (File.Exists(Path.Combine(mirror, "shallow")))
        {
            full.Add("--unshallow");
        }

        full.AddRange(["origin", "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*"]);
        var all = await RunAsync(full, mirror, NetworkTimeout, ct).ConfigureAwait(false);
        if (await HasCommitAsync(mirror, sha, ct).ConfigureAwait(false))
        {
            return (GitStatus.Ok, "full fetch");
        }

        var allStatus = all.Succeeded ? GitStatus.Missing : Classify(all);
        return (allStatus is GitStatus.Ok or GitStatus.Refused ? GitStatus.Missing : allStatus, all.Succeeded ? $"commit {sha} is not in the repository" : all.Tail(5));
    }, ct);

    /// <summary><c>git worktree add --detach &lt;dir&gt; &lt;sha&gt;</c>; returns the HEAD of the new worktree.</summary>
    public Task<string> AddWorktreeAsync(string mirror, string sha, string directory, CancellationToken ct) => WithLock(mirror, async () =>
    {
        await RunAsync(["-C", mirror, "worktree", "prune"], mirror, LocalTimeout, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(directory))!);
        Check(await RunAsync(["-C", mirror, "worktree", "add", "--detach", "-f", directory, sha], mirror, NetworkTimeout, ct).ConfigureAwait(false), "git worktree add");
        var head = await RunAsync(["-C", directory, "rev-parse", "HEAD"], directory, LocalTimeout, ct).ConfigureAwait(false);
        return head.Output.Trim().Split('\n')[^1].Trim();
    }, ct);

    /// <summary><c>git worktree remove --force</c>, then prune; the folder is gone afterwards even if git failed.</summary>
    public Task RemoveWorktreeAsync(string mirror, string directory, CancellationToken ct) => WithLock(mirror, async () =>
    {
        if (Directory.Exists(mirror))
        {
            await RunAsync(["-C", mirror, "worktree", "remove", "--force", directory], mirror, LocalTimeout, ct).ConfigureAwait(false);
        }

        WorkspaceManager.DeleteTree(directory);
        if (Directory.Exists(mirror))
        {
            await RunAsync(["-C", mirror, "worktree", "prune"], mirror, LocalTimeout, ct).ConfigureAwait(false);
        }

        return true;
    }, ct);

    /// <summary>
    /// <c>git rev-parse HEAD</c> and whether <c>git status --porcelain</c> is not empty, for a local folder (P01 §3.1 step 4);
    /// (null, null) when the folder is not in a git repository.
    /// </summary>
    public async Task<(string? Commit, bool? Dirty)> LocalStateAsync(string directory, CancellationToken ct)
    {
        var head = await RunAsync(["-C", directory, "rev-parse", "HEAD"], directory, LocalTimeout, ct).ConfigureAwait(false);
        if (!head.Succeeded)
        {
            return (null, null);
        }

        var status = await RunAsync(["-C", directory, "status", "--porcelain"], directory, LocalTimeout, ct).ConfigureAwait(false);
        return (head.Output.Trim().Split('\n')[^1].Trim(), status.Succeeded ? status.Output.Trim().Length > 0 : null);
    }

    /// <summary><c>git --version</c>, e.g. <c>2.46.0</c>; null when git does not run.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        var result = await RunAsync(["--version"], Path.GetTempPath(), LocalTimeout, ct).ConfigureAwait(false);
        var match = System.Text.RegularExpressions.Regex.Match(result.Output, @"git version (\d+(\.\d+)*)");
        return result.Succeeded && match.Success ? match.Groups[1].Value : null;
    }

    private static void Check(ProcessResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new IOException($"{what} failed: {result.Tail(5)}");
        }
    }

    private static async Task<T> WithLock<T>(string mirror, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = MirrorLocks.GetOrAdd(Path.GetFullPath(mirror), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
