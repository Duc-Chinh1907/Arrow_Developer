using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Arrow.Common;
using Arrow.Common.Events;
using Spectre.Console;

namespace Arrow.Cli.Rendering;

/// <summary>
/// The progress of <c>arrow gen</c> (P08 §3): one line per block (<c>✓ Khối 1 …</c>, <c>✓ Khối 2 …</c>, <c>▶ Khối 3 c0: …</c> with
/// one indented line per repair round, <c>✓ Khối 4 …</c>), prefixed with <c>[i/n]</c> when the run has several classes, and with
/// the time in plain mode. Events wait in a bounded channel and are printed by one reader (P07 §10): when the console falls
/// behind, Info events are dropped rather than holding the worker threads. In rich mode, a run with several classes is drawn as a
/// live table, one row per class, until every class is done.
/// </summary>
internal sealed class GenEventPrinter : IProgress<ArrowEvent>, IAsyncDisposable
{
    /// <summary>Events waiting to be printed.</summary>
    public const int Capacity = 1000;

    private readonly IRenderer _renderer;
    private readonly bool _verbose;
    private readonly Func<DateTimeOffset> _now;
    private readonly Channel<ArrowEvent> _channel;
    private readonly Task _reader;
    private readonly Dictionary<string, List<string>> _quality = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastCall = new(StringComparer.Ordinal);
    private readonly HashSet<string> _c0Printed = new(StringComparer.Ordinal);
    private Task? _live;
    private int _total;
    private int _done;
    private int _dropped;
    private long _queued;
    private long _printed;
    private volatile bool _stopLive;
    private IReadOnlyList<string> _targets = [];

    public GenEventPrinter(IRenderer renderer, bool verbose, Func<DateTimeOffset>? now = null, int capacity = Capacity)
    {
        _channel = Channel.CreateBounded<ArrowEvent>(new BoundedChannelOptions(capacity) { SingleReader = true });
        _renderer = renderer;
        _verbose = verbose;
        _now = now ?? (() => DateTimeOffset.Now);
        _reader = Task.Run(ReadAsync);
    }

    /// <summary>Info events dropped because the console was behind.</summary>
    public int Dropped => _dropped;

    public void Report(ArrowEvent value)
    {
        if (_channel.Writer.TryWrite(value))
        {
            Interlocked.Increment(ref _queued);
            return;
        }

        if (value.Level == EventLevel.Info)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        // Warnings and errors are never dropped: wait for room.
        _channel.Writer.WriteAsync(value).AsTask().GetAwaiter().GetResult();
        Interlocked.Increment(ref _queued);
    }

    /// <summary>
    /// Prints every queued event and ends the live table, so that a question to the user is not drawn over (P08 §4). Lines after
    /// the question print normally.
    /// </summary>
    public async Task SettleAsync()
    {
        while (Interlocked.Read(ref _printed) < Interlocked.Read(ref _queued) && !_reader.IsCompleted)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        _stopLive = true;
        if (_live is not null)
        {
            await _live.ConfigureAwait(false);
        }
    }

    /// <summary>Prints what is left and stops the live table; call before the command prints its summary.</summary>
    public async Task FlushAsync()
    {
        _channel.Writer.TryComplete();
        await _reader.ConfigureAwait(false);
        Interlocked.Exchange(ref _done, Math.Max(_done, _total));
        if (_live is not null)
        {
            await _live.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await FlushAsync().ConfigureAwait(false);

    private async Task ReadAsync()
    {
        await foreach (var value in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Print(value);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // A closed console must not break the run.
            }
            finally
            {
                Interlocked.Increment(ref _printed);
            }
        }
    }

    private bool LiveOn => _live is not null && !_stopLive && Volatile.Read(ref _done) < _total;

    private void Print(ArrowEvent e)
    {
        var name = e.CutFqn is { } cut ? cut[(cut.LastIndexOf('.') + 1)..] : null;
        var prefix = e.Index is { } i && e.Total is > 1 ? $"[{i}/{e.Total}] " : "";
        switch (e.Type)
        {
            case ArrowEventTypes.RunTargetsResolved when e.Data.TryGetValue("targets", out var t) && t is IReadOnlyCollection<string> list:
                _total = list.Count;
                _targets = list.ToList();
                foreach (var fqn in _targets)
                {
                    _rows[fqn] = "chờ";
                }

                return;
            case ArrowEventTypes.RunTargetStarted when _live is null && !_stopLive && _total > 1 && _renderer.Ansi is { } ansi:
                // The live table starts with the first class, after the cost question (P07 §4).
                _rows[e.CutFqn ?? ""] = "bắt đầu";
                _live = Task.Run(() => RunLive(ansi, _targets));
                return;
            case ArrowEventTypes.RunTargetStarted:
                Block(e, Kind.Plain, $"{prefix}ARROW · {name} ({PackageOf(e.CutFqn)})");
                return;
            case ArrowEventTypes.RunTargetDone:
                Interlocked.Increment(ref _done);
                Row(e.CutFqn, (e.Data.GetValueOrDefault("result") as string) ?? "xong");
                return;
            case ArrowEventTypes.B1Done:
                Block(e, Kind.Ok, prefix + "Khối 1  " + e.Message);
                return;
            case ArrowEventTypes.B2Response:
                var tokens = Convert.ToInt64(e.Data.GetValueOrDefault("prompt_tokens") ?? 0, CultureInfo.InvariantCulture)
                             + Convert.ToInt64(e.Data.GetValueOrDefault("completion_tokens") ?? 0, CultureInfo.InvariantCulture);
                var latency = Convert.ToDouble(e.Data.GetValueOrDefault("latency_seconds") ?? 0.0, CultureInfo.InvariantCulture);
                _lastCall[e.CutFqn ?? ""] = $"{tokens / 1000.0:0.0}k token · {latency.ToString("0.0", CultureInfo.InvariantCulture)} s"
                                           + (e.Data.GetValueOrDefault("replayed") is true ? " (phát lại)" : "");
                return;
            case ArrowEventTypes.B2Candidate:
                // Only c0 is block 2; the candidates of the repair rounds show in the round lines of block 3.
                if (_c0Printed.Add(e.CutFqn ?? ""))
                {
                    var call = _lastCall.GetValueOrDefault(e.CutFqn ?? "", "");
                    Block(e, e.Level == EventLevel.Info ? Kind.Ok : Kind.Fail, prefix + "Khối 2  c0 · " + (call.Length > 0 ? call + " · " : "") + e.Message);
                }

                return;
            case ArrowEventTypes.B3Verified when e.Data.GetValueOrDefault("round") is 0:
                var state = e.Data.GetValueOrDefault("state") as string ?? "?";
                var signature = e.Data.GetValueOrDefault("signature") as string;
                var detail = signature is null ? "" : " · " + (signature.StartsWith(state + " | ", StringComparison.Ordinal) ? signature[(state.Length + 3)..] : signature);
                Block(e, state == "VALID" ? Kind.Ok : Kind.Step, prefix + "Khối 3  c0: " + state + detail);
                return;
            case ArrowEventTypes.B3Round:
                Block(e, Kind.Plain, prefix + "          " + e.Message);
                return;
            case ArrowEventTypes.B3Final:
                var valid = (e.Data.GetValueOrDefault("final_state") as string) == "VALID";
                Block(e, valid ? Kind.Ok : Kind.Fail, prefix + "Khối 3  " + e.Message);
                return;
            case ArrowEventTypes.B4ToolDone:
                lock (_quality)
                {
                    (_quality.TryGetValue(e.CutFqn ?? "", out var parts) ? parts : _quality[e.CutFqn ?? ""] = []).Add(e.Message);
                }

                Row(e.CutFqn, "khối 4: " + e.Message);
                return;
            case ArrowEventTypes.B4Done:
                List<string>? done;
                lock (_quality)
                {
                    _quality.Remove(e.CutFqn ?? "", out done);
                }

                if (done is { Count: > 0 })
                {
                    Block(e, Kind.Ok, prefix + "Khối 4  " + string.Join(" · ", done));
                }

                return;
        }

        if (e.Level != EventLevel.Info)
        {
            Block(e, e.Level == EventLevel.Warning ? Kind.Warn : Kind.Fail, prefix + e.Message);
            return;
        }

        Row(e.CutFqn, e.Message);
        if (_verbose)
        {
            Block(e, Kind.Step, prefix + e.Message);
        }
    }

    private enum Kind
    {
        Plain,
        Ok,
        Warn,
        Step,
        Fail,
    }

    /// <summary>A line of a target: printed now, or shown in its row while the live table runs.</summary>
    private void Block(ArrowEvent e, Kind kind, string text)
    {
        if (LiveOn && kind is not (Kind.Warn or Kind.Fail))
        {
            Row(e.CutFqn, text.TrimStart());
            return;
        }

        Line(kind, text);
    }

    private void Line(Kind kind, string text)
    {
        var line = _renderer.Mode == RenderMode.Plain ? $"{_now().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {text}" : text;
        switch (kind)
        {
            case Kind.Ok:
                _renderer.Ok(line);
                break;
            case Kind.Warn:
                _renderer.Warn(line);
                break;
            case Kind.Step:
                _renderer.Step(line);
                break;
            case Kind.Fail:
                _renderer.Fail(line, null);
                break;
            default:
                _renderer.Line(line);
                break;
        }
    }

    private void Row(string? cut, string text)
    {
        if (cut is not null && _rows.ContainsKey(cut))
        {
            _rows[cut] = text;
        }
    }

    /// <summary>The live table of P08 §3.2 for several classes: one row per class, refreshed until every class is done.</summary>
    private async Task RunLive(IAnsiConsole ansi, IReadOnlyList<string> order)
    {
        var table = new Table().AddColumn("Class").AddColumn("Đang làm");
        foreach (var fqn in order)
        {
            table.AddRow(Markup.Escape(fqn[(fqn.LastIndexOf('.') + 1)..]), "chờ");
        }

        await ansi.Live(table).AutoClear(false).StartAsync(async context =>
        {
            while (true)
            {
                for (var i = 0; i < order.Count; i++)
                {
                    table.UpdateCell(i, 1, Markup.Escape(_rows.GetValueOrDefault(order[i], "")));
                }

                context.Refresh();
                if (Volatile.Read(ref _done) >= _total || _stopLive)
                {
                    break;
                }

                await Task.Delay(200).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static string PackageOf(string? fqn) => fqn is null || !fqn.Contains('.', StringComparison.Ordinal) ? "(default package)" : fqn[..fqn.LastIndexOf('.')];
}
