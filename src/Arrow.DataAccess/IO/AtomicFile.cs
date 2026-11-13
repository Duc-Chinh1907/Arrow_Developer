using System.Collections.Concurrent;
using System.Text;

namespace Arrow.DataAccess.IO;

/// <summary>
/// Atomic file replace of P00 §5: write a temporary file next to the target, flush, then move it over the target.
/// A reader never sees a half-written file, and a crash leaves either the old or the new content.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes UTF-8 text without BOM; CRLF becomes LF (P00 §5).</summary>
    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, Utf8NoBom.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)));

    /// <summary>Writes the bytes exactly as given.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        lock (FileLocks.For(full))
        {
            // A unique name, so that two processes writing the same file never share a temporary file.
            var temp = $"{full}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                MoveWithRetry(temp, full);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private static void MoveWithRetry(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                // from ARROW_Framework/src/Arrow.DataAccess/Common/FileHelpers.cs:
                // Windows briefly refuses the replace while another process has the target open.
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One lock object per file path, so threads of this process do not interleave writes to the same file.</summary>
internal static class FileLocks
{
    private static readonly ConcurrentDictionary<string, object> Locks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static object For(string fullPath) => Locks.GetOrAdd(fullPath, _ => new object());
}
