using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;
using Arrow.DataAccess.Tools;
using Arrow.IntegrationTests.Fixtures;

namespace Arrow.IntegrationTests.Source;

/// <summary>
/// Getting the code with real git and real files (P01 §3). The repository is a bare repository created in a temp folder, so no
/// network is needed; <see cref="GitClient"/> accepts any URL, while <see cref="GitRemote"/> (https only) is covered by unit tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectSourceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "arrow-source-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly ProcessRunner _runner = new();

    public void Dispose() => WorkspaceManager.DeleteTree(_temp);

    private GitClient Git()
    {
        var git = new ToolLocator(Environment.GetEnvironmentVariable).FindOnPath("git");
        Assert.SkipWhen(git is null, "git is not on PATH");
        return new GitClient(_runner, git!);
    }

    private async Task<string> RunGit(GitClient git, string dir, params string[] args)
    {
        var result = await git.RunAsync(["-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", .. args], dir, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Output}");
        return result.Output.Trim();
    }

    /// <summary>A bare repository with main (2 commits), a branch, a lightweight and an annotated tag.</summary>
    private async Task<(string Url, string First, string Second, string Feature)> CreateOriginAsync(GitClient git)
    {
        var work = Path.Combine(_temp, "work");
        Directory.CreateDirectory(work);
        await RunGit(git, work, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(work, "pom.xml"), "<project/>");
        await RunGit(git, work, "add", ".");
        await RunGit(git, work, "commit", "-q", "-m", "first");
        var first = await RunGit(git, work, "rev-parse", "HEAD");
        await RunGit(git, work, "tag", "light");
        await RunGit(git, work, "tag", "-a", "v1.0", "-m", "release");
        File.WriteAllText(Path.Combine(work, "README.md"), "second");
        await RunGit(git, work, "add", ".");
        await RunGit(git, work, "commit", "-q", "-m", "second");
        var second = await RunGit(git, work, "rev-parse", "HEAD");
        await RunGit(git, work, "checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(work, "F.txt"), "f");
        await RunGit(git, work, "add", ".");
        await RunGit(git, work, "commit", "-q", "-m", "feature");
        var feature = await RunGit(git, work, "rev-parse", "HEAD");
        await RunGit(git, work, "checkout", "-q", "main");
        var bare = Path.Combine(_temp, "origin.git");
        await RunGit(git, _temp, "clone", "-q", "--bare", work, bare);
        return (new Uri(bare).AbsoluteUri, first, second, feature);
    }

    [Fact]
    public async Task ResolvesFetchesAndChecksOutCommits()
    {
        var git = Git();
        var ct = TestContext.Current.CancellationToken;
        var (url, first, second, feature) = await CreateOriginAsync(git);
        var mirror = Path.Combine(_temp, "mirrors", "origin.git");
        await git.EnsureMirrorAsync(mirror, url, ct);
        await git.EnsureMirrorAsync(mirror, url, ct);

        var head = GitRefResolver.ResolveHead((await git.LsRemoteAsync(mirror, ["--symref", "origin", "HEAD"], ct)).Output);
        Assert.Equal(new ResolvedRef(second, "refs/heads/main"), head);
        Assert.Equal(feature, GitRefResolver.Resolve("feature", (await git.LsRemoteRefAsync(mirror, "feature", ct)).Output).Sha);
        Assert.Equal(first, GitRefResolver.Resolve("v1.0", (await git.LsRemoteRefAsync(mirror, "v1.0", ct)).Output).Sha);
        Assert.Equal(first, GitRefResolver.Resolve("light", (await git.LsRemoteRefAsync(mirror, "light", ct)).Output).Sha);
        Assert.Null(GitRefResolver.Resolve("nope", (await git.LsRemoteRefAsync(mirror, "nope", ct)).Output).Sha);

        Assert.Equal(GitStatus.Ok, (await git.FetchAsync(mirror, feature, ct)).Status);
        Assert.Equal(GitStatus.Ok, (await git.FetchAsync(mirror, first, ct)).Status);
        Assert.Equal(GitStatus.Missing, (await git.FetchAsync(mirror, new string('a', 40), ct)).Status);

        var base1 = Path.Combine(_temp, "work-run", "base");
        Assert.Equal(feature, await git.AddWorktreeAsync(mirror, feature, base1, ct));
        Assert.True(File.Exists(Path.Combine(base1, "F.txt")));
        var base2 = Path.Combine(_temp, "work-run2", "base");
        Assert.Equal(first, await git.AddWorktreeAsync(mirror, first, base2, ct));
        Assert.False(File.Exists(Path.Combine(base2, "README.md")));

        await git.RemoveWorktreeAsync(mirror, base1, ct);
        await git.RemoveWorktreeAsync(mirror, base2, ct);
        Assert.False(Directory.Exists(base1));
        Assert.False(Directory.Exists(base2));
    }

    [Fact]
    public async Task CopiesALocalFixtureWithoutChangingIt()
    {
        var git = Git();
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(FixtureRepo.FixturesDir, "maven-multi-junit5");
        var before = FixtureRepo.FingerprintOf(fixture);
        var source = new ProjectSource(Path.Combine(_temp, "mirrors"), git, new WorkspaceManager(), []);

        // From inside a module folder without a build file of its own, the root is the reactor.
        var target = new ProjectTarget(ProjectSourceKind.LocalDirectory, Path.Combine(fixture, "shop-core", "src", "main", "java"), null, "OrderService");
        var checkout = await source.CheckoutAsync(target, Path.Combine(_temp, "run", "base"), null, ct);

        Assert.Equal(Path.GetFullPath(fixture), checkout.ProjectRoot);
        Assert.Equal("maven-multi-junit5", checkout.PathName);
        Assert.True(File.Exists(Path.Combine(checkout.Root, "shop-core", "pom.xml")));
        Assert.StartsWith("sha256:", checkout.SourceTreeHash, StringComparison.Ordinal);
        // The fixtures live in this git repository, so a commit is known (display only).
        Assert.NotNull(checkout.GitCommit);

        var again = await source.CheckoutAsync(target, Path.Combine(_temp, "run2", "base"), null, ct);
        Assert.Equal(checkout.SourceTreeHash, again.SourceTreeHash);

        await source.CleanupAsync(checkout, ct);
        Assert.False(Directory.Exists(checkout.Root));
        Assert.Equal(before, FixtureRepo.FingerprintOf(fixture));
    }

    [Fact]
    public async Task FolderWithoutBuildFileIsBlocked()
    {
        var source = new ProjectSource(Path.Combine(_temp, "mirrors"), null, new WorkspaceManager(), []);
        Directory.CreateDirectory(Path.Combine(_temp, "empty", "a", "b"));
        var error = await Assert.ThrowsAsync<ArrowException>(() => source.CheckoutAsync(
            new ProjectTarget(ProjectSourceKind.LocalDirectory, Path.Combine(_temp, "empty", "a", "b"), null, "X"), Path.Combine(_temp, "base"), null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.B1BuildRootNotFound, error.Code);
    }
}
