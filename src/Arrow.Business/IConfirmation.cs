namespace Arrow.Business;

/// <summary>
/// Questions a use case asks the user (P08 §9). The CLI implements it with Spectre prompts, or answers without asking when there is
/// no keyboard or <c>--yes</c> was given. Business never depends on the console.
/// </summary>
public interface IConfirmation
{
    /// <summary>True when the user can be asked; false without a keyboard (P08 §4.3).</summary>
    bool CanAsk { get; }

    /// <summary>A yes/no question; returns <paramref name="defaultAnswer"/> when the user cannot be asked or <c>--yes</c> was given.</summary>
    Task<bool> ConfirmAsync(string question, bool defaultAnswer, CancellationToken ct);

    /// <summary>Lets the user pick one option; returns its index, or null when the user cannot be asked or gave up.</summary>
    Task<int?> ChooseOneAsync(string question, IReadOnlyList<string> options, CancellationToken ct);
}

/// <summary>Answers without asking: defaults for yes/no, no choice. Used without a keyboard, and by tests.</summary>
public sealed class NoConfirmation(bool yes = false) : IConfirmation
{
    public bool CanAsk => false;

    public Task<bool> ConfirmAsync(string question, bool defaultAnswer, CancellationToken ct) => Task.FromResult(yes || defaultAnswer);

    public Task<int?> ChooseOneAsync(string question, IReadOnlyList<string> options, CancellationToken ct) => Task.FromResult<int?>(null);
}
