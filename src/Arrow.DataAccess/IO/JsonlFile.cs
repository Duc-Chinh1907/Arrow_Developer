using System.Text;
using System.Text.Json;
using Arrow.Common.Json;

namespace Arrow.DataAccess.IO;

/// <summary>Records read back from a JSONL file, and how many lines could not be read.</summary>
public sealed record JsonlContent<T>(IReadOnlyList<T> Items, int SkippedLines);

/// <summary>
/// JSON Lines files such as <c>events.jsonl</c> and <c>history.jsonl</c>: one compact JSON value per line, UTF-8, LF.
/// Appends are serialized per file; reads skip lines that do not parse, such as a line cut off by a crash.
/// </summary>
public static class JsonlFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Appends one record. <paramref name="transform"/> can rewrite the line first, e.g. to redact it.</summary>
    public static void Append<T>(string path, T value, Func<string, string>? transform = null)
    {
        var line = ArrowJson.Serialize(value, indented: false);
        if (transform is not null)
        {
            line = transform(line);
        }

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = Utf8NoBom.GetBytes(line + "\n");
        lock (FileLocks.For(full))
        {
            using var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
        }
    }

    /// <summary>Reads every record; a missing file gives an empty list.</summary>
    public static JsonlContent<T> ReadAll<T>(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            return new JsonlContent<T>([], 0);
        }

        string text;
        lock (FileLocks.For(full))
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Utf8NoBom);
            text = reader.ReadToEnd();
        }

        var items = new List<T>();
        var skipped = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                items.Add(ArrowJson.Deserialize<T>(line));
            }
            catch (JsonException)
            {
                skipped++;
            }
        }

        return new JsonlContent<T>(items, skipped);
    }
}
