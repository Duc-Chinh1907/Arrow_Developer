using Arrow.Business;
using Arrow.Cli.Rendering;

namespace Arrow.Cli.Prompts;

/// <summary>
/// Asks through <paramref name="inner"/> once the progress printer has printed its queue and closed its live table, so that a
/// question of <c>arrow gen</c> (cost, write) is never drawn over by progress lines (P08 §4).
/// </summary>
internal sealed class SettlingConfirmation(IConfirmation inner, GenEventPrinter printer) : IConfirmation
{
    public bool CanAsk => inner.CanAsk;

    public async Task<bool> ConfirmAsync(string question, bool defaultAnswer, CancellationToken ct)
    {
        await printer.SettleAsync().ConfigureAwait(false);
        return await inner.ConfirmAsync(question, defaultAnswer, ct).ConfigureAwait(false);
    }

    public async Task<int?> ChooseOneAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        await printer.SettleAsync().ConfigureAwait(false);
        return await inner.ChooseOneAsync(question, options, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<int>?> ChooseManyAsync(string question, IReadOnlyList<string> options, IReadOnlyList<bool> preselected, CancellationToken ct)
    {
        await printer.SettleAsync().ConfigureAwait(false);
        return await inner.ChooseManyAsync(question, options, preselected, ct).ConfigureAwait(false);
    }
}
