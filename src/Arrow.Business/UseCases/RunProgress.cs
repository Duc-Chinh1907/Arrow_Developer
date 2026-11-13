using Arrow.Common.Events;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.UseCases;

/// <summary>Reports an event to the caller and appends it to <c>events.jsonl</c> (P07 §10).</summary>
internal sealed class RunProgress(IRunStore runs, RunHandle run, IProgress<ArrowEvent>? inner) : IProgress<ArrowEvent>
{
    private readonly object _gate = new();

    public void Report(ArrowEvent value)
    {
        lock (_gate)
        {
            runs.AppendEvent(run, value);
        }

        inner?.Report(value);
    }
}
