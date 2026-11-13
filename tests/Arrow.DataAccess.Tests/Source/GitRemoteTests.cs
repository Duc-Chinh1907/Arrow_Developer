using Arrow.Common.Errors;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Source;

namespace Arrow.DataAccess.Tests.Source;

/// <summary>Repository URLs, ref resolution and git error classes (P01 §3.2, §11).</summary>
public class GitRemoteTests
{
    [Fact]
    public void AcceptsHttpsUrlsAndDropsDotGit()
    {
        var remote = GitRemote.Parse("https://GitHub.com/gephi/graphstore.git/");
        Assert.Equal(("https://github.com/gephi/graphstore", "github.com", "gephi/graphstore", "graphstore"), (remote.Url, remote.Host, remote.RepoPath, remote.Name));
        Assert.Equal(Path.Combine("/m", "github.com", "gephi", "graphstore.git"), remote.MirrorPath("/m"));
        Assert.Equal("https://gitlab.example.com:8443/group/sub/repo", GitRemote.Parse("https://gitlab.example.com:8443/group/sub/repo").Url);
    }

    [Theory]
    [InlineData("https://user:ghp_secret@github.com/a/b")]
    [InlineData("https://ghp_secret@github.com/a/b")]
    public void RefusesUrlsWithCredentials(string url)
    {
        var error = Assert.Throws<ArrowException>(() => GitRemote.Parse(url));
        Assert.Equal(ErrorCodes.B1UrlHasCredentials, error.Code);
        Assert.DoesNotContain("ghp_secret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://github.com/a/b")]
    [InlineData("git@github.com:a/b.git")]
    [InlineData("https://github.com/")]
    [InlineData("D:/src/repo")]
    public void RefusesOtherUrls(string url) => Assert.Equal(ErrorCodes.CliArgs, Assert.Throws<ArrowException>(() => GitRemote.Parse(url)).Code);

    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";
    private const string Sha3 = "3333333333333333333333333333333333333333";

    // P01 §11: branch, annotated and lightweight tag, full SHA, short SHA (error), no ref.
    [Fact]
    public void ResolvesBranchesAndTags()
    {
        var output = $"{Sha1}\trefs/heads/release\n{Sha2}\trefs/tags/release\n{Sha3}\trefs/tags/release^{{}}\n";
        Assert.Equal(new ResolvedRef(Sha1, "refs/heads/release"), GitRefResolver.Resolve("release", output));

        // Annotated tag: the commit it points to (^{}), not the tag object.
        var tags = $"{Sha2}\trefs/tags/v1.0\n{Sha3}\trefs/tags/v1.0^{{}}\n{Sha1}\trefs/tags/light\n";
        Assert.Equal(new ResolvedRef(Sha3, "refs/tags/v1.0"), GitRefResolver.Resolve("v1.0", tags));
        Assert.Equal(new ResolvedRef(Sha1, "refs/tags/light"), GitRefResolver.Resolve("light", tags));
        Assert.Equal(new ResolvedRef(null, null), GitRefResolver.Resolve("nope", tags));
        Assert.Equal(Sha1, GitRefResolver.Resolve("refs/tags/light", tags).Sha);
    }

    [Fact]
    public void RecognizesShasAndTheDefaultBranch()
    {
        Assert.True(GitRefResolver.IsFullSha(Sha1));
        Assert.False(GitRefResolver.IsFullSha("1234567"));
        Assert.True(GitRefResolver.IsShortSha("abc1234"));
        Assert.False(GitRefResolver.IsShortSha("main"));
        Assert.False(GitRefResolver.IsShortSha("abc12"));

        var head = GitRefResolver.ResolveHead($"ref: refs/heads/main\tHEAD\n{Sha2}\tHEAD\n");
        Assert.Equal(new ResolvedRef(Sha2, "refs/heads/main"), head);
    }

    private static ProcessResult R(int? exit, string output, bool timedOut = false) => new(exit, timedOut, output, TimeSpan.Zero);

    // Parity: Block1Tests.ClassifiesBuildFailures (git part). "Repository not found" is now AuthRequired (B1_AUTH_REQUIRED, P01 §3.2
    // step 6): GitHub gives the same answer for a private repository without login and for a missing one.
    [Fact]
    public void ClassifiesGitFailures()
    {
        Assert.Equal(GitStatus.AuthRequired, GitClient.Classify(R(128, "remote: Repository not found.\nfatal: repository 'https://github.com/x/y/' not found")));
        Assert.Equal(GitStatus.Network, GitClient.Classify(R(128, "fatal: unable to access 'https://github.com/x/y/': Could not resolve host: github.com")));
        Assert.Equal(GitStatus.Refused, GitClient.Classify(R(128, "fatal: remote error: upload-pack: not our ref 0123")));
        Assert.Equal(GitStatus.AuthRequired, GitClient.Classify(R(128, "fatal: could not read Username for 'https://github.com': terminal prompts disabled")));
        Assert.Equal(GitStatus.AuthRequired, GitClient.Classify(R(128, "fatal: unable to access 'https://x/': The requested URL returned error: 403")));
        Assert.Equal(GitStatus.Network, GitClient.Classify(R(null, "", timedOut: true)));
        Assert.Equal(GitStatus.Missing, GitClient.Classify(R(128, "fatal: invalid refspec")));
        Assert.Equal(GitStatus.Ok, GitClient.Classify(R(0, "")));
    }
}
