using Arrow.Common.Analysis;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.Tests.Storage;

public sealed class BaselineCacheStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-cache-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static readonly string Key = new('a', 64);

    private static BaselineCacheEntry Entry(string key) => new(
        key, "20261116-0140-abcd", new DateTimeOffset(2026, 11, 16, 1, 40, 0, TimeSpan.Zero), "0.1.0", 2,
        ["a.ATest#ok"], ["a.ATest#flaky"], ["a.ATest#broken"], [12.3, 8.0]);

    [Fact]
    public void AStoredEntryIsReadBack()
    {
        var store = new BaselineCacheStore(new ArrowHome(_root));
        Assert.Null(store.Get(Key));

        store.Put(Entry(Key));

        var read = store.Get(Key)!;
        Assert.Equal(("20261116-0140-abcd", 2), (read.RunId, read.Runs));
        Assert.Equal(["a.ATest#ok"], read.PassingInAllRuns);
        Assert.Equal(["a.ATest#flaky"], read.Flaky);
        Assert.Equal(["a.ATest#broken"], read.FailingInAllRuns);
        Assert.True(File.Exists(Path.Combine(_root, "cache", "baseline", Key + ".json")));
    }

    // A file that cannot be read means "measure again", never an error.
    [Fact]
    public void ABrokenFileIsTreatedAsMissing()
    {
        var store = new BaselineCacheStore(new ArrowHome(_root));
        var file = Path.Combine(_root, "cache", "baseline", Key + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ not json");
        Assert.Null(store.Get(Key));

        // An entry stored under another key name is not trusted either.
        store.Put(Entry(new string('b', 64)));
        File.Copy(Path.Combine(_root, "cache", "baseline", new string('b', 64) + ".json"), file, overwrite: true);
        Assert.Null(store.Get(Key));
    }

    [Fact]
    public void AKeyIsAHexDigest() =>
        Assert.Throws<ArgumentException>(() => new BaselineCacheStore(new ArrowHome(_root)).Get("../escape"));
}
