using System.Collections.Concurrent;
using Arrow.Common.Logging;

namespace Arrow.DataAccess.JavaHelper;

/// <summary>
/// java-helper processes of one run (P10 §5, P07 §7): with <c>--jobs n</c> each running target rents its own process,
/// at most <c>n</c> of them; processes start lazily and are reused by later targets.
/// </summary>
public sealed class JavaHelperPool : IAsyncDisposable
{
    private readonly JavaHelperOptions _options;
    private readonly IAppLog? _log;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<JavaHelperClient> _idle = [];
    private readonly ConcurrentBag<JavaHelperClient> _all = [];

    public JavaHelperPool(JavaHelperOptions options, int size, IAppLog? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        _options = options;
        _log = log;
        Size = size;
        _slots = new SemaphoreSlim(size, size);
    }

    /// <summary>Most processes at the same time.</summary>
    public int Size { get; }

    /// <summary>Processes created so far.</summary>
    public int Created => _all.Count;

    /// <summary>Waits for a free process; dispose the lease to give it back.</summary>
    public async Task<Lease> RentAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        if (!_idle.TryTake(out var client))
        {
            client = new JavaHelperClient(_options, _log);
            _all.Add(client);
        }

        return new Lease(this, client);
    }

    /// <summary>Stops every process of the pool.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _all)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _slots.Dispose();
    }

    private void Return(JavaHelperClient client)
    {
        _idle.Add(client);
        _slots.Release();
    }

    /// <summary>A process rented from the pool.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly JavaHelperPool _pool;
        private bool _returned;

        internal Lease(JavaHelperPool pool, JavaHelperClient client)
        {
            _pool = pool;
            Client = client;
        }

        public JavaHelperClient Client { get; }

        public IJavaHelper Helper => Client;

        public void Dispose()
        {
            if (!_returned)
            {
                _returned = true;
                _pool.Return(Client);
            }
        }
    }
}
