using Arrow.Common.Errors;
using Arrow.Common.Secrets;
using Arrow.DataAccess.Security;

namespace Arrow.Business.UseCases;

/// <summary><c>arrow secrets list|set|remove</c> (P09 §4.4).</summary>
public sealed class SecretsUseCase(ISecretStore store)
{
    /// <summary>Stored keys and <c>ARROW_SECRET_*</c> variables, last characters only.</summary>
    public IReadOnlyList<SecretInfo> List() => store.List();

    /// <summary>Stores a key and returns the hint to show, e.g. <c>...9f1c</c>.</summary>
    public string Set(string reference, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArrowException(ErrorCodes.CliArgs, "Khoá API rỗng.", HelpArgs("secrets set"));
        }

        var secret = new SecretValue(trimmed);
        Guard(() => store.Set(reference, secret));
        return secret.Hint;
    }

    /// <summary>Deletes a stored key; false when there was none.</summary>
    public bool Remove(string reference) => Guard(() => store.Remove(reference));

    private static void Guard(Action action) => Guard(() =>
    {
        action();
        return true;
    });

    /// <summary>An invalid reference is an argument error of the user (exit code 2), not an internal error.</summary>
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            throw new ArrowException(ErrorCodes.CliArgs, "Tên khoá không hợp lệ: chỉ dùng a-z, 0-9 và '-'.", HelpArgs("secrets"), ex);
        }
    }

    private static Dictionary<string, string> HelpArgs(string command) => new() { ["help_command"] = $"arrow {command} --help" };
}
