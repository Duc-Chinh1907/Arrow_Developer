using System.Text.RegularExpressions;
using Arrow.Common.Errors;

namespace Arrow.DataAccess.Source;

/// <summary>A checked repository URL (P01 §3.2 step 1).</summary>
/// <param name="Url">https URL without a trailing <c>.git</c> or <c>/</c>.</param>
/// <param name="Host">Host name, lower case.</param>
/// <param name="RepoPath">Path on the host, e.g. <c>gephi/graphstore</c>.</param>
public sealed record GitRemote(string Url, string Host, string RepoPath)
{
    /// <summary>Last part of the path, e.g. <c>graphstore</c>.</summary>
    public string Name => RepoPath[(RepoPath.LastIndexOf('/') + 1)..];

    /// <summary>Mirror folder below <c>ARROW_HOME/mirrors</c>: <c>&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;.git</c>.</summary>
    public string MirrorPath(string mirrorsDir) =>
        Path.Combine([mirrorsDir, Host, .. RepoPath.Split('/')[..^1], Name + ".git"]);

    /// <summary>
    /// Checks <paramref name="url"/>: only <c>https://</c> is accepted, a trailing <c>.git</c> is dropped, and a URL with credentials
    /// (<c>https://user:token@…</c>) is refused with <c>B1_URL_HAS_CREDENTIALS</c> without echoing it.
    /// </summary>
    public static GitRemote Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArrowException(ErrorCodes.CliArgs, $"--repo phải là URL https://, ví dụ https://github.com/owner/repo (nhận được: {Safe(url)})",
                new Dictionary<string, string> { ["help_command"] = "arrow gen --help" });
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new ArrowException(ErrorCodes.B1UrlHasCredentials);
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        if (path.Length == 0 || path.Split('/').Any(p => p is "" or "." or ".."))
        {
            throw new ArrowException(ErrorCodes.CliArgs, $"URL không chỉ tới một repo: {Safe(url)}",
                new Dictionary<string, string> { ["help_command"] = "arrow gen --help" });
        }

        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new GitRemote($"https://{host}{port}/{path}", host, path);
    }

    private static string Safe(string url) => Regex.Replace(url, "//[^/@]*@", "//***@");
}

/// <summary>What a ref resolves to.</summary>
/// <param name="Sha">Full commit SHA; null when not found.</param>
/// <param name="Name">The ref as matched, e.g. <c>refs/heads/main</c>; null for a SHA.</param>
public sealed record ResolvedRef(string? Sha, string? Name);

/// <summary>Resolves a branch, tag or SHA from the output of <c>git ls-remote</c> (P01 §3.2 step 3).</summary>
public static partial class GitRefResolver
{
    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex FullSha();

    [GeneratedRegex("^[0-9a-fA-F]{7,39}$")]
    private static partial Regex ShortSha();

    /// <summary>A full 40-character SHA, used as it is.</summary>
    public static bool IsFullSha(string? gitRef) => gitRef is not null && FullSha().IsMatch(gitRef);

    /// <summary>
    /// A short SHA (7–39 hex characters) that is not also a branch or tag: refused with a hint to use the full SHA.
    /// Call it only after the ref was not found by name.
    /// </summary>
    public static bool IsShortSha(string? gitRef) => gitRef is not null && ShortSha().IsMatch(gitRef);

    /// <summary>
    /// The commit of <paramref name="gitRef"/> in the output of <c>git ls-remote origin &lt;ref&gt;</c>, in the order
    /// <c>refs/heads/&lt;ref&gt;</c>, <c>refs/tags/&lt;ref&gt;^{}</c> (annotated tag), <c>refs/tags/&lt;ref&gt;</c>.
    /// A ref already starting with <c>refs/</c> matches as written.
    /// </summary>
    public static ResolvedRef Resolve(string gitRef, string lsRemoteOutput)
    {
        var refs = Lines(lsRemoteOutput);
        string[] wanted = gitRef.StartsWith("refs/", StringComparison.Ordinal)
            ? [gitRef + "^{}", gitRef]
            : [$"refs/heads/{gitRef}", $"refs/tags/{gitRef}^{{}}", $"refs/tags/{gitRef}"];
        foreach (var name in wanted)
        {
            if (refs.TryGetValue(name, out var sha))
            {
                return new ResolvedRef(sha, name.EndsWith("^{}", StringComparison.Ordinal) ? name[..^3] : name);
            }
        }

        return new ResolvedRef(null, null);
    }

    /// <summary>The default branch and its commit, from <c>git ls-remote --symref origin HEAD</c>.</summary>
    public static ResolvedRef ResolveHead(string lsRemoteSymrefOutput)
    {
        string? branch = null;
        foreach (var line in lsRemoteSymrefOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ref: ", StringComparison.Ordinal) && trimmed.EndsWith("HEAD", StringComparison.Ordinal))
            {
                branch = trimmed["ref: ".Length..].Split('\t', ' ')[0];
            }
        }

        return new ResolvedRef(Lines(lsRemoteSymrefOutput).GetValueOrDefault("HEAD"), branch);
    }

    private static Dictionary<string, string> Lines(string output)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2 && FullSha().IsMatch(parts[0]))
            {
                refs.TryAdd(parts[1], parts[0].ToLowerInvariant());
            }
        }

        return refs;
    }
}
