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

    public void Report(ArrowEvent value)
    {
        // The command prints its own summary after B1.Done; run-level events are written to events.jsonl but shown only with --verbose.
        if (value.Type == ArrowEventTypes.B1Done)
        {
            return;
        }

        if (value.Level == EventLevel.Info && value.Type.StartsWith("RUN.", StringComparison.Ordinal) && !verbose)
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
