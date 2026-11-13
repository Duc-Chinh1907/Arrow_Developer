using System.Text;
using Arrow.Common.Errors;

namespace Arrow.DataAccess.Apply;

/// <summary>Line endings of a written test, taken from the CUT (P06 §5).</summary>
public enum LineEndings
{
    Lf,
    Crlf,
}

/// <summary>Writes a new test file into the user's project (P06 §5). The only writer into the project.</summary>
public interface IProjectFileWriter
{
    /// <summary>
    /// Creates <paramref name="relativePath"/> under <paramref name="projectRoot"/>. Never overwrites: an existing file gives
    /// <c>W_TARGET_EXISTS</c>; a path that leaves the project (<c>..</c>, absolute, a symlink or junction on the way) gives
    /// <c>W_TARGET_OUTSIDE_PROJECT</c>; any other I/O error gives <c>W_IO</c>. Nothing is left behind on failure.
    /// </summary>
    Task<string> WriteNewAsync(string projectRoot, string relativePath, string content, LineEndings eol, CancellationToken ct);
}

/// <summary>
/// <see cref="IProjectFileWriter"/>: UTF-8 without BOM, the line endings of the CUT, exactly one final newline, written to
/// <c>.&lt;name&gt;.arrow-tmp</c> with <see cref="FileMode.CreateNew"/> and moved without overwrite.
/// </summary>
public sealed class ProjectFileWriter : IProjectFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>LF unless the text uses CRLF.</summary>
    public static LineEndings EndingsOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? LineEndings.Crlf : LineEndings.Lf;

    /// <summary>The content as written: the line endings asked for and one final newline.</summary>
    public static string Normalize(string content, LineEndings eol)
    {
        var lf = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', '\r', ' ', '\t') + "\n";
        return eol == LineEndings.Crlf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    /// <inheritdoc />
    public async Task<string> WriteNewAsync(string projectRoot, string relativePath, string content, LineEndings eol, CancellationToken ct)
    {
        var target = Target(projectRoot, relativePath);
        var directory = Path.GetDirectoryName(target)!;
        var tmp = Path.Combine(directory, "." + Path.GetFileName(target) + ".arrow-tmp");
        try
        {
            CreateDirectories(Path.GetFullPath(projectRoot), directory);
            if (File.Exists(target))
            {
                throw new ArrowException(ErrorCodes.WTargetExists, relativePath);
            }

            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(Normalize(content, eol));
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tmp, target, overwrite: false);
            return target;
        }
        catch (IOException) when (File.Exists(target))
        {
            DeleteQuietly(tmp);
            throw new ArrowException(ErrorCodes.WTargetExists, relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tmp);
            throw new ArrowException(ErrorCodes.WIo, $"{relativePath}: {ex.Message}");
        }
        catch (Exception)
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    /// <summary>
    /// The absolute target, after checking that it stays in the project: relative, no <c>..</c>, inside the root after
    /// normalizing, and no existing folder on the way is a symlink or junction (P06 §3 step 4).
    /// </summary>
    public static string Target(string projectRoot, string relativePath)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal) || parts.Any(p => p is ".." or ""))
        {
            throw Outside(relativePath);
        }

        var target = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw Outside(relativePath);
        }

        for (var dir = Path.GetDirectoryName(target); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget is not null)
            {
                throw Outside(relativePath);
            }
        }

        return target;
    }

    private static void CreateDirectories(string root, string directory)
    {
        var missing = new Stack<string>();
        for (var dir = directory; !Directory.Exists(dir) && dir.Length > root.Length; dir = Path.GetDirectoryName(dir)!)
        {
            missing.Push(dir);
        }

        while (missing.TryPop(out var dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static ArrowException Outside(string relativePath) => new(ErrorCodes.WTargetOutsideProject, relativePath);

    private static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the file starts with a dot and ends in .arrow-tmp.
        }
    }
}
