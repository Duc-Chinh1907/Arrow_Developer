using Arrow.Common;
using Arrow.Common.Events;

namespace Arrow.Cli.Rendering;

/// <summary>
/// Prints progress events as they arrive (P07 §10): <c>▶ message</c>, warnings and errors with their own marker. Events come from
/// worker threads, so printing is serialized, and it never blocks on anything but the console.
/// In JSON mode the renderer writes to stderr, so stdout keeps the result object only.
/// </summary>
internal sealed class EventPrinter(IRenderer renderer, bool verbose) : IProgress<ArrowEvent>
{
    private readonly object _gate = new();

    /// <summary>Steps inside a block, shown only with <c>--verbose</c>; the block results are always shown.</summary>
    private static readonly HashSet<string> DetailTypes = new(StringComparer.Ordinal)
    {
        ArrowEventTypes.B2Prompt, ArrowEventTypes.B2Calling, ArrowEventTypes.B3VerifyStep, ArrowEventTypes.ApplyWritten, ArrowEventTypes.ApplySkipped,
    };

    public void Report(ArrowEvent value)
    {
        // The command prints its own summary after B1.Done; run-level events are written to events.jsonl but shown only with --verbose.
        if (value.Type == ArrowEventTypes.B1Done)
        {
            return;
        }

        // A repair round prints one B3.Round line; its B3.Verified line (shown for c0) would repeat it.
        var repeatsRound = value.Type == ArrowEventTypes.B3Verified && value.Data.TryGetValue("round", out var round) && round is int r && r > 0;
        if (value.Level == EventLevel.Info && !verbose && (value.Type.StartsWith("RUN.", StringComparison.Ordinal) || DetailTypes.Contains(value.Type) || repeatsRound))
        {
            return;
        }

        lock (_gate)
        {
            switch (value.Level)
            {
                case EventLevel.Warning:
                    renderer.Warn(value.Message);
                    break;
                case EventLevel.Error:
                    renderer.Fail(value.Message, null);
                    break;
                default:
                    renderer.Step(value.Message);
                    break;
            }
        }
    }
}
