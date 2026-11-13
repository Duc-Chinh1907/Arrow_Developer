namespace Arrow.DataAccess.Source;

/// <summary>Copies and deletes working copies (P01 §10 <c>IWorkspaceManager</c>).</summary>
public interface IWorkspaceManager
{
    /// <summary>Copies the tree at <paramref name="source"/> to <paramref name="destination"/>, which must not exist; returns <paramref name="destination"/>.</summary>
    Task<string> CloneAsync(string source, string destination, CancellationToken ct);

    /// <summary>Deletes a tree, read-only files included; a missing folder is fine.</summary>
    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>
/// <see cref="IWorkspaceManager"/> with plain file copies. Symbolic links are copied as links; on macOS and Linux the execute
/// bits are kept, so wrappers stay runnable.
/// </summary>
public sealed class WorkspaceManager : IWorkspaceManager
{
    /// <summary>Files copied in parallel.</summary>
    private const int Parallelism = 8;

    public async Task<string> CloneAsync(string source, string destination, CancellationToken ct)
    {
        var from = Path.GetFullPath(source);
        var to = Path.GetFullPath(destination);
        if (Directory.Exists(to))
        {
            throw new IOException($"{to} already exists");
        }

        var files = new List<(string From, string To)>();
        var links = new List<(string Link, string Target)>();
        Directory.CreateDirectory(to);
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var target = Path.Combine(to, Path.GetRelativePath(from, dir));
            Directory.CreateDirectory(target);
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var copy = Path.Combine(target, entry.Name);
                if (entry.LinkTarget is { } link)
                {
                    links.Add((copy, link));
                }
                else if (entry is DirectoryInfo)
                {
                    stack.Push(entry.FullName);
                }
                else
                {
                    files.Add((entry.FullName, copy));
                }
            }
        }

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, (file, _) =>
        {
            CopyFile(file.From, file.To);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        foreach (var (link, target) in links)
        {
            CreateLink(link, target);
        }

        return to;
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DeleteTree(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a tree. Read-only files (git objects on Windows) are made writable first; links are removed, never followed.
    /// A file still locked after a few tries (an antivirus scan, a build process that has not exited) raises <see cref="IOException"/>.
    /// </summary>
    public static void DeleteTree(string path)
    {
        var full = Path.GetFullPath(path);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(full) || new FileInfo(full).LinkTarget is not null)
                {
                    File.Delete(full);
                    return;
                }

                if (!Directory.Exists(full))
                {
                    return;
                }

                ClearReadOnly(new DirectoryInfo(full));
                Directory.Delete(full, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    /// <summary>Copies one file; keeps the Unix file mode (execute bits of <c>mvnw</c>, <c>gradlew</c>).</summary>
    public static void CopyFile(string from, string to)
    {
        File.Copy(from, to, overwrite: false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        else
        {
            File.SetAttributes(to, File.GetAttributes(to) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>Creates a link with the same (relative or absolute) target text; on Windows without the privilege, copies the target instead.</summary>
    public static void CreateLink(string link, string target)
    {
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, target));
        try
        {
            if (Directory.Exists(resolved))
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
        }
        catch (IOException) when (OperatingSystem.IsWindows() && File.Exists(resolved))
        {
            // Creating links needs developer mode or admin rights on Windows: a copy of the file works for a build.
            File.Copy(resolved, link);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && File.Exists(resolved))
        {
            File.Copy(resolved, link);
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (entry is DirectoryInfo child)
            {
                ClearReadOnly(child);
            }
        }
    }
}
