using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Arrow.DataAccess.Source;

/// <summary>A file to copy. <see cref="Path"/> is relative to the project root and uses <c>/</c>.</summary>
public sealed record TreeFile(string Path, long Size, DateTime ModifiedUtc);

/// <summary>A symbolic link inside the project, copied as a link.</summary>
public sealed record TreeLink(string Path, string Target);

/// <summary>
/// The files of a local project that are copied into the working copy (P01 §3.1 step 2), and the <c>source_tree_hash</c> of them.
/// </summary>
/// <param name="Root">Absolute project root.</param>
/// <param name="Files">Files, sorted by path.</param>
/// <param name="Links">Links whose target is inside the project.</param>
/// <param name="Warnings">Skipped links that point outside the project.</param>
public sealed record LocalProjectTree(string Root, IReadOnlyList<TreeFile> Files, IReadOnlyList<TreeLink> Links, IReadOnlyList<string> Warnings)
{
    /// <summary>Size above which the user is asked before copying (P01 §3.1 step 5).</summary>
    public const long LargeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Folders never copied, wherever they are.</summary>
    public static IReadOnlySet<string> SkippedFolders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".gradle", ".idea", ".vscode", "node_modules", ".arrow-work",
    };

    /// <summary>
    /// Build output folders, skipped only next to a build file or a <c>src/</c> folder. A Java package called <c>build</c> or
    /// <c>out</c> deep inside <c>src/main/java</c> is still copied.
    /// </summary>
    public static IReadOnlySet<string> OutputFolders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "target", "build", "out", "bin" };

    private static readonly string[] BuildFiles = ["pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"];

    /// <summary>Total size of the files.</summary>
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>
    /// <c>sha256:</c> of the sorted list of (relative path, size, modification time) of the files: it changes when a file is added,
    /// removed or edited, and does not depend on the order in which the folder was listed.
    /// </summary>
    public string SourceTreeHash => SourceTreeHashWithout(new HashSet<string>());

    /// <summary>
    /// <see cref="SourceTreeHash"/> without the files of <paramref name="excluded"/> (relative paths with <c>/</c>): resuming a run
    /// ignores the test files that the run itself wrote into the project (P07 §5.2).
    /// </summary>
    public string SourceTreeHashWithout(IReadOnlySet<string> excluded)
    {
        var text = new StringBuilder();
        foreach (var file in Files.Where(f => !excluded.Contains(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            text.Append(file.Path).Append('\t')
                .Append(file.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(file.ModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var link in Links.OrderBy(l => l.Path, StringComparer.Ordinal))
        {
            text.Append(link.Path).Append("\t->\t").Append(link.Target).Append('\n');
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Lists the project at <paramref name="root"/> with the filters of P01 §3.1 step 2.</summary>
    public static LocalProjectTree Scan(string root)
    {
        var full = Path.GetFullPath(root);
        var files = new List<TreeFile>();
        var links = new List<TreeLink>();
        var warnings = new List<string>();
        var stack = new Stack<string>([full]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
            var hasBuildMarker = entries.Any(e => e is DirectoryInfo && e.Name == "src" || e is FileInfo && BuildFiles.Contains(e.Name));
            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(full, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is { } target)
                {
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry.FullName)!, target));
                    if (Inside(full, resolved))
                    {
                        links.Add(new TreeLink(relative, target));
                    }
                    else
                    {
                        warnings.Add($"Bỏ qua link {relative}: đích nằm ngoài project ({resolved})");
                    }
                }
                else if (entry is DirectoryInfo)
                {
                    if (!SkippedFolders.Contains(entry.Name) && !(hasBuildMarker && OutputFolders.Contains(entry.Name)))
                    {
                        stack.Push(entry.FullName);
                    }
                }
                else if (entry is FileInfo file && !Skipped(file.Name))
                {
                    files.Add(new TreeFile(relative, file.Length, file.LastWriteTimeUtc));
                }
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new LocalProjectTree(full, files, links, warnings);
    }

    /// <summary>Copies the listed files and links to <paramref name="destination"/>, which must not exist.</summary>
    public async Task CopyAsync(string destination, CancellationToken ct)
    {
        var to = Path.GetFullPath(destination);
        if (Directory.Exists(to))
        {
            throw new IOException($"{to} already exists");
        }

        Directory.CreateDirectory(to);
        foreach (var dir in Files.Select(f => Path.GetDirectoryName(f.Path)).Concat(Links.Select(l => Path.GetDirectoryName(l.Path))).Distinct())
        {
            Directory.CreateDirectory(Path.Combine(to, dir ?? ""));
        }

        await Parallel.ForEachAsync(Files, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, (file, _) =>
        {
            WorkspaceManager.CopyFile(Path.Combine(Root, file.Path), Path.Combine(to, file.Path));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        foreach (var link in Links)
        {
            WorkspaceManager.CreateLink(Path.Combine(to, link.Path), link.Target);
        }
    }

    private static bool Skipped(string name) =>
        name.EndsWith(".class", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name == ".git";

    private static bool Inside(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>Finds the project root from the folder the user gave (P01 §3.1 step 1).</summary>
public static class ProjectRootFinder
{
    /// <summary>Levels searched above the folder.</summary>
    public const int MaxLevels = 5;

    private static readonly string[] BuildFiles = ["pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"];

    /// <summary>True when <paramref name="dir"/> has a Maven or Gradle build file.</summary>
    public static bool HasBuildFile(string dir) => BuildFiles.Any(f => File.Exists(Path.Combine(dir, f)));

    /// <summary>
    /// <paramref name="location"/> when it has a build file; otherwise the highest folder of the first run of consecutive folders with
    /// a build file found within <see cref="MaxLevels"/> levels above it; otherwise <paramref name="location"/> again when a sub-folder has
    /// a build file (a repository whose build lives in <c>lib/</c>). Null when there is none (<c>B1_BUILD_ROOT_NOT_FOUND</c>).
    /// </summary>
    public static string? Find(string location)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(location));
        if (!dir.Exists)
        {
            return null;
        }

        if (HasBuildFile(dir.FullName))
        {
            return dir.FullName;
        }

        string? found = null;
        var current = dir.Parent;
        for (var level = 1; level <= MaxLevels && current is not null; level++, current = current.Parent)
        {
            if (HasBuildFile(current.FullName))
            {
                found = current.FullName;
            }
            else if (found is not null)
            {
                break;
            }
        }

        if (found is null && Build.BuildLayout.Scan(dir.FullName).Dirs.Count > 0)
        {
            return dir.FullName;
        }

        return found;
    }
}
