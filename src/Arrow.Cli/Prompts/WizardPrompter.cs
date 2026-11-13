using Arrow.Common.Secrets;
using Spectre.Console;

namespace Arrow.Cli.Prompts;

/// <summary>
/// The questions of <c>arrow setup</c> and <c>arrow models add</c> (P08 §2.5). Every method returns null (or -1) when the user asks to go back:
/// <c>&lt;</c> in a text question, or the last option "Quay lại" of a list.
/// </summary>
internal interface IWizardPrompter
{
    /// <summary>Free text; <paramref name="defaultValue"/> is used for an empty answer. Null means "back".</summary>
    string? Ask(string question, string? defaultValue = null);

    /// <summary>A key typed with the characters hidden. Empty gives <see cref="AskedSecret.Empty"/>; "back" gives <see cref="AskedSecret.Back"/>.</summary>
    AskedSecret AskSecret(string question);

    /// <summary>The index of the chosen option, or -1 for "back".</summary>
    int Select(string question, IReadOnlyList<string> options, bool canGoBack);

    /// <summary>Yes or no.</summary>
    bool Confirm(string question, bool defaultValue);
}

/// <summary>The answer to <see cref="IWizardPrompter.AskSecret"/>.</summary>
/// <param name="Value">The key; null when empty or when the user went back.</param>
/// <param name="WentBack">The user typed <c>&lt;</c>.</param>
internal sealed record AskedSecret(SecretValue? Value, bool WentBack)
{
    public static AskedSecret Empty { get; } = new(null, false);

    public static AskedSecret Back { get; } = new(null, true);
}

/// <summary><see cref="IWizardPrompter"/> with Spectre prompts. Keys are masked while typed (P08 §2.5).</summary>
internal sealed class SpectreWizardPrompter : IWizardPrompter
{
    public const string BackOption = "← Quay lại";

    public string? Ask(string question, string? defaultValue = null)
    {
        var prompt = new TextPrompt<string>(Markup.Escape(question) + " [grey](gõ < để quay lại)[/]").AllowEmpty();
        if (defaultValue is not null)
        {
            prompt.DefaultValue(defaultValue).ShowDefaultValue();
        }

        var answer = AnsiConsole.Prompt(prompt).Trim();
        return answer == "<" ? null : answer;
    }

    public AskedSecret AskSecret(string question)
    {
        var answer = AnsiConsole.Prompt(new TextPrompt<string>(Markup.Escape(question) + " [grey](Enter để bỏ qua, < để quay lại)[/]").AllowEmpty().Secret()).Trim();
        return answer switch
        {
            "<" => AskedSecret.Back,
            "" => AskedSecret.Empty,
            _ => new AskedSecret(new SecretValue(answer), false),
        };
    }

    public int Select(string question, IReadOnlyList<string> options, bool canGoBack)
    {
        var choices = canGoBack ? [.. options, BackOption] : options.ToList();
        var chosen = AnsiConsole.Prompt(new SelectionPrompt<string>().Title(Markup.Escape(question)).AddChoices(choices));
        return chosen == BackOption ? -1 : choices.IndexOf(chosen);
    }

    public bool Confirm(string question, bool defaultValue) => AnsiConsole.Confirm(Markup.Escape(question), defaultValue);
}
