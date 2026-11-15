using System.Text.Json;
using Arrow.Common.Analysis;
using Arrow.Common.Json;
using Arrow.DataAccess.IO;

namespace Arrow.DataAccess.Storage;

/// <summary>Baselines kept across runs (P01 §6.2), by cache key.</summary>
public interface IBaselineCache
{
    /// <summary>The entry of <paramref name="key"/>, or null when there is none or it cannot be read.</summary>
    BaselineCacheEntry? Get(string key);

    /// <summary>Stores <paramref name="entry"/> under its key, replacing an older one.</summary>
    void Put(BaselineCacheEntry entry);
}

/// <summary>
/// <see cref="IBaselineCache"/> in <c>ARROW_HOME/cache/baseline/&lt;key&gt;.json</c>. Files are written atomically, so two runs that
/// store the same key at once leave one whole file. A file that cannot be read is treated as missing: the baseline is measured again.
/// </summary>
public sealed class BaselineCacheStore(ArrowHome home) : IBaselineCache
{
    private string Dir => Path.Combine(home.CacheDir, "baseline");

    public BaselineCacheEntry? Get(string key)
    {
        var file = FileOf(key);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var entry = ArrowJson.Deserialize<BaselineCacheEntry>(File.ReadAllText(file));
            return entry.Key == key ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Put(BaselineCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            AtomicFile.WriteAllText(FileOf(entry.Key), ArrowJson.Serialize(entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache only saves time; failing to store it must not fail the run.
        }
    }

    private string FileOf(string key)
    {
        if (key.Length == 0 || !key.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("A cache key is a hex digest.", nameof(key));
        }

        return Path.Combine(Dir, key + ".json");
    }
}
