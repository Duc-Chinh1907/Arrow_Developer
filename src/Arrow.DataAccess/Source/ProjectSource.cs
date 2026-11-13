using Arrow.Common;
using Arrow.Common.Errors;

namespace Arrow.DataAccess.Source;

/// <summary>The working copy <c>work/&lt;run_id&gt;/base</c> and where it came from (P01 §3).</summary>
/// <param name="Root">Absolute folder of the copy.</param>
/// <param name="Kind">Local folder or git repository.</param>
/// <param name="ProjectRoot">Local project root the copy was made from; null for a repository.</param>
/// <param name="PathName">Folder name of the project root, or the repository name.</param>
/// <param name="Url">Repository URL without credentials; null for a local folder.</param>
/// <param name="GitRef">Ref as resolved, e.g. <c>refs/heads/main</c>; null for a local folder or a SHA.</param>
/// <param name="GitCommit">Commit of the copy; for a local folder, HEAD of its repository (display only).</param>
/// <param name="Dirty">Local folder only: uncommitted changes exist.</param>
/// <param name="SourceTreeHash"><c>sha256:…</c> of the copied files; null for a repository.</param>
/// <param name="Mirror">Mirror folder of a repository, needed to remove the worktree; null for a local folder.</param>
/// <param name="Warnings">Warnings to print, such as skipped links.</param>
public sealed record SourceCheckout(
    string Root,
    ProjectSourceKind Kind,
    string? ProjectRoot,
    string PathName,
    string? Url,
    string? GitRef,
    string? GitCommit,
    bool? Dirty,
    string? SourceTreeHash,
    string? Mirror,
    IReadOnlyList<string> Warnings);

/// <summary>Gets the code of a project into a working copy (P01 §10).</summary>
public interface IProjectSource
{
    /// <summary>
    /// Copies a local folder (P01 §3.1) or checks out a repository (§3.2) into <paramref name="destination"/>. Known failures raise
    /// <see cref="ArrowException"/> with a <c>B1_*</c> code. <paramref name="confirmLargeCopy"/> is asked before copying more than
    /// 2 GB; returning false cancels.
    /// </summary>
    Task<SourceCheckout> CheckoutAsync(ProjectTarget target, string destination, Func<long, Task<bool>>? confirmLargeCopy, CancellationToken ct);

    /// <summary>Deletes the working copy; for a repository also removes the worktree from the mirror.</summary>
    Task CleanupAsync(SourceCheckout checkout, CancellationToken ct);
}

/// <summary><see cref="IProjectSource"/> on the file system and git.</summary>
/// <param name="mirrorsDir"><c>ARROW_HOME/mirrors</c>.</param>
/// <param name="git">git client; null when git is not installed, which only matters for <c>--repo</c>.</param>
/// <param name="workspaces">Deletes copies.</param>
/// <param name="retryDelays">Waits between network attempts; P01 §3.2 step 4 uses 10 s then 30 s. Tests pass zero.</param>
public sealed class ProjectSource(string mirrorsDir, GitClient? git, IWorkspaceManager workspaces, IReadOnlyList<TimeSpan>? retryDelays = null) : IProjectSource
{
    /// <summary>Network attempts in total (P01 §3.2 step 4).</summary>
    public const int NetworkAttempts = 3;

    private readonly IReadOnlyList<TimeSpan> _delays = retryDelays ?? [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public Task<SourceCheckout> CheckoutAsync(ProjectTarget target, string destination, Func<long, Task<bool>>? confirmLargeCopy, CancellationToken ct) =>
        target.Kind == ProjectSourceKind.LocalDirectory
            ? CheckoutLocalAsync(target.Location, destination, confirmLargeCopy, ct)
            : CheckoutGitAsync(target.Location, target.GitRef, destination, ct);

    public async Task CleanupAsync(SourceCheckout checkout, CancellationToken ct)
    {
        if (checkout.Mirror is not null && git is not null)
        {
            await git.RemoveWorktreeAsync(checkout.Mirror, checkout.Root, ct).ConfigureAwait(false);
        }
        else
        {
            await workspaces.DeleteAsync(checkout.Root, ct).ConfigureAwait(false);
        }
    }

    private async Task<SourceCheckout> CheckoutLocalAsync(string location, string destination, Func<long, Task<bool>>? confirmLargeCopy, CancellationToken ct)
    {
        var root = ProjectRootFinder.Find(location)
            ?? throw new ArrowException(ErrorCodes.B1BuildRootNotFound, $"Không tìm thấy pom.xml hay build.gradle ở {location} và 5 thư mục cha của nó.");

        LocalProjectTree tree;
        try
        {
            tree = LocalProjectTree.Scan(root);
            if (tree.TotalBytes > LocalProjectTree.LargeBytes && confirmLargeCopy is not null && !await confirmLargeCopy(tree.TotalBytes).ConfigureAwait(false))
            {
                throw new OperationCanceledException("The user declined to copy a large project.");
            }

            await tree.CopyAsync(destination, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArrowException(ErrorCodes.B1Workspace, $"Không chép được project vào {destination}: {ex.Message}", inner: ex);
        }

        var (commit, dirty) = git is null ? (null, null) : await git.LocalStateAsync(root, ct).ConfigureAwait(false);
        return new SourceCheckout(Path.GetFullPath(destination), ProjectSourceKind.LocalDirectory, root, new DirectoryInfo(root).Name, null, null,
            commit, dirty, tree.SourceTreeHash, null, tree.Warnings);
    }

    private async Task<SourceCheckout> CheckoutGitAsync(string url, string? gitRef, string destination, CancellationToken ct)
    {
        var remote = GitRemote.Parse(url);
        if (git is null)
        {
            throw new ArrowException(ErrorCodes.B1CloneFailed, "Máy không có git, nên không tải được repo. Cài git hoặc đặt java.git_path.");
        }

        var mirror = remote.MirrorPath(mirrorsDir);
        try
        {
            await git.EnsureMirrorAsync(mirror, remote.Url, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new ArrowException(ErrorCodes.B1Workspace, $"Không tạo được mirror {mirror}: {ex.Message}", inner: ex);
        }

        var (sha, refName) = await ResolveAsync(mirror, gitRef, ct).ConfigureAwait(false);
        await RetryAsync(async () => await git.FetchAsync(mirror, sha, ct).ConfigureAwait(false), gitRef ?? sha, ct).ConfigureAwait(false);

        string head;
        try
        {
            head = await git.AddWorktreeAsync(mirror, sha, destination, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new ArrowException(ErrorCodes.B1Workspace, $"Không tạo được bản sao {destination}: {ex.Message}", inner: ex);
        }

        if (!string.Equals(head, sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArrowException(ErrorCodes.B1CommitMissing, $"HEAD của bản sao là {head}, không phải {sha}.", Ref(gitRef ?? sha));
        }

        return new SourceCheckout(Path.GetFullPath(destination), ProjectSourceKind.GitRepository, null, remote.Name, remote.Url, refName,
            sha, null, null, mirror, []);
    }

    /// <summary>The commit to check out (P01 §3.2 step 3).</summary>
    private async Task<(string Sha, string? RefName)> ResolveAsync(string mirror, string? gitRef, CancellationToken ct)
    {
        if (GitRefResolver.IsFullSha(gitRef))
        {
            return (gitRef!.ToLowerInvariant(), null);
        }

        var shown = gitRef ?? "HEAD";
        var output = await RetryAsync(async () =>
        {
            var result = gitRef is null
                ? await git!.LsRemoteAsync(mirror, ["--symref", "origin", "HEAD"], ct).ConfigureAwait(false)
                : await git!.LsRemoteRefAsync(mirror, gitRef, ct).ConfigureAwait(false);
            var status = GitClient.Classify(result);
            return (status == GitStatus.Refused ? GitStatus.Missing : status, result.Succeeded ? result.Output : result.Tail(5));
        }, shown, ct).ConfigureAwait(false);

        var resolved = gitRef is null ? GitRefResolver.ResolveHead(output) : GitRefResolver.Resolve(gitRef, output);
        if (resolved.Sha is not null)
        {
            return (resolved.Sha, resolved.Name);
        }

        var detail = GitRefResolver.IsShortSha(gitRef)
            ? $"{gitRef} là SHA rút gọn; git chỉ tải được commit theo SHA đầy đủ 40 ký tự."
            : $"Repo không có nhánh hay tag {shown}.";
        throw new ArrowException(ErrorCodes.B1CommitMissing, detail, Ref(shown));
    }

    /// <summary>Runs a network step up to <see cref="NetworkAttempts"/> times while it fails with a network error.</summary>
    private async Task<string> RetryAsync(Func<Task<(GitStatus Status, string Detail)>> step, string shownRef, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (status, detail) = await step().ConfigureAwait(false);
            switch (status)
            {
                case GitStatus.Ok:
                    return detail;
                case GitStatus.AuthRequired:
                    throw new ArrowException(ErrorCodes.B1AuthRequired, $"Repo không tồn tại hoặc cần đăng nhập: {detail}");
                case GitStatus.Network when attempt < NetworkAttempts:
                    await Task.Delay(_delays.Count == 0 ? TimeSpan.Zero : _delays[Math.Min(attempt - 1, _delays.Count - 1)], ct).ConfigureAwait(false);
                    break;
                case GitStatus.Network:
                    throw new ArrowException(ErrorCodes.B1CloneFailed, $"Không tải được repo sau {attempt} lần thử: {detail}");
                default:
                    throw new ArrowException(ErrorCodes.B1CommitMissing, detail, Ref(shownRef));
            }
        }
    }

    private static Dictionary<string, string> Ref(string value) => new() { ["ref"] = value };
}
