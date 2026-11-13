using Arrow.Business;
using Spectre.Console;

namespace Arrow.Cli.Prompts;

/// <summary>
/// <see cref="IConfirmation"/> with Spectre prompts, used when the terminal has a keyboard (P08 §4.3, §9). Without a keyboard, or
/// with <c>--yes</c> or <c>--json</c>, <see cref="NoConfirmation"/> is used instead and nothing waits for an answer.
/// </summary>
internal sealed class SpectrePrompter : IConfirmation
{
    public bool CanAsk => true;

    public Task<bool> ConfirmAsync(string question, bool defaultAnswer, CancellationToken ct) =>
        Task.FromResult(AnsiConsole.Confirm(Markup.Escape(question), defaultAnswer));

    public Task<int?> ChooseOneAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        var chosen = AnsiConsole.Prompt(new SelectionPrompt<string>().Title(Markup.Escape(question)).AddChoices(options));
        return Task.FromResult<int?>(options.ToList().IndexOf(chosen));
    }

    public Task<IReadOnlyList<int>?> ChooseManyAsync(string question, IReadOnlyList<string> options, IReadOnlyList<bool> preselected, CancellationToken ct)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title(Markup.Escape(question))
            .NotRequired()
            .InstructionsText("[grey](phím cách để chọn, Enter để xác nhận)[/]")
            .AddChoices(options);
        for (var i = 0; i < options.Count; i++)
        {
            if (preselected[i])
            {
                prompt.Select(options[i]);
            }
        }

        var chosen = AnsiConsole.Prompt(prompt);
        return Task.FromResult<IReadOnlyList<int>?>(chosen.Select(c => options.ToList().IndexOf(c)).Order().ToList());
    }
}
