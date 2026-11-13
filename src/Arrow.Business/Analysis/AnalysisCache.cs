using System.Collections.Concurrent;

namespace Arrow.Business.Analysis;

/// <summary>
/// Shares block 1 results inside one run (P01 §7): the key is <c>(source_tree_hash, module_root)</c>. The first caller of a key
/// computes the value; callers with the same key wait for it (<see cref="SemaphoreSlim"/> per key) instead of doing it again.
/// A factory that throws (cancellation) leaves the key empty, so the next caller computes it.
/// </summary>
public sealed class AnalysisCache<TValue>
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Cache key of a module.</summary>
    public static string Key(string sourceTreeHash, string moduleRoot) => sourceTreeHash + "|" + moduleRoot;

    /// <summary>The value of <paramref name="key"/>, computed by <paramref name="factory"/> when nobody did yet. <c>Created</c> is true for that caller only.</summary>
    public async Task<(TValue Value, bool Created)> GetOrCreateAsync(string key, Func<CancellationToken, Task<TValue>> factory, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.HasValue)
            {
                return (entry.Value!, false);
            }

            var value = await factory(ct).ConfigureAwait(false);
            entry.Value = value;
            entry.HasValue = true;
            return (value, true);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool HasValue { get; set; }

        public TValue? Value { get; set; }
    }
}
