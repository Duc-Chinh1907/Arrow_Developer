namespace Arrow.Common.Secrets;

/// <summary>
/// An API key (P09 §4.5). <see cref="ToString"/> returns <c>***</c>, so a key never reaches a log or a message by accident.
/// Only the LLM clients call <see cref="Reveal"/>.
/// </summary>
public sealed class SecretValue
{
    private readonly string _value;

    public SecretValue(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    /// <summary>Last characters shown to the user, e.g. <c>...2a4b</c> (P00 §5); <c>***</c> for short keys.</summary>
    public string Hint => MaskForDisplay(_value);

    /// <summary>The plain key. Only for the code that sends it to the provider.</summary>
    public string Reveal() => _value;

    public override string ToString() => "***";

    /// <summary><c>...</c> plus the last 4 characters; keys of 8 characters or fewer show nothing.</summary>
    public static string MaskForDisplay(string secret) => secret.Length <= 8 ? "***" : "..." + secret[^4..];
}

/// <summary>Where a key was found (P09 §4.3).</summary>
public enum SecretSource
{
    /// <summary><c>ARROW_SECRET_&lt;REF&gt;</c>.</summary>
    ArrowEnvironment,

    /// <summary><c>OPENAI_API_KEY</c> or <c>ANTHROPIC_API_KEY</c>.</summary>
    ProviderEnvironment,

    /// <summary><c>ARROW_HOME/secrets/</c>.</summary>
    Store,
}

/// <summary>One line of <c>arrow secrets list</c>.</summary>
public sealed record SecretInfo(string Reference, string Hint, SecretSource Source);
