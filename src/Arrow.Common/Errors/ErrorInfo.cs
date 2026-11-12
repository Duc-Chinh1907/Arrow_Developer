namespace Arrow.Common.Errors;

/// <summary>
/// An error as written into result files and printed by the CLI: <c>{ "code", "message", "hint", "log" }</c> (P07 §6).
/// </summary>
/// <param name="Code">One of <see cref="ErrorCodes"/>.</param>
/// <param name="Message">What went wrong, in the UI language.</param>
/// <param name="Hint">How to fix it, in the UI language; null when there is nothing the user can do.</param>
/// <param name="Log">Log file with the details, relative to the run folder; null when there is none.</param>
public sealed record ErrorInfo(string Code, string Message, string? Hint, string? Log);

/// <summary>
/// Error raised for a known condition, carrying an <see cref="ErrorCodes"/> code.
/// Callers turn it into an <see cref="ErrorInfo"/> through <see cref="ErrorCatalog.Describe(ArrowException, string?)"/>.
/// </summary>
public sealed class ArrowException : Exception
{
    public ArrowException(string code, string? detail = null, IReadOnlyDictionary<string, string>? args = null, Exception? inner = null)
        : base(detail is null ? code : $"{code}: {detail}", inner)
    {
        Code = code;
        Detail = detail;
        Args = args ?? new Dictionary<string, string>();
    }

    /// <summary>The error code.</summary>
    public string Code { get; }

    /// <summary>Specific description of this occurrence, e.g. the first compiler error; replaces the catalog message when set.</summary>
    public string? Detail { get; }

    /// <summary>Values for the <c>{name}</c> placeholders of the catalog entry.</summary>
    public IReadOnlyDictionary<string, string> Args { get; }
}
