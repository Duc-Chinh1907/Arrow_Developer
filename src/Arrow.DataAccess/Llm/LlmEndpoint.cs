using Arrow.Common.Secrets;

namespace Arrow.DataAccess.Llm;

/// <summary>Where and how an HTTP client talks to a provider.</summary>
/// <param name="BaseUrl">Base URL, e.g. <c>https://api.teamorouter.com/v1</c> or <c>https://api.anthropic.com</c>.</param>
/// <param name="ModelId">Model name at the provider.</param>
/// <param name="Key">API key; null for a provider that needs none, such as Ollama.</param>
/// <param name="SecretRef">Name of the key, used in the hint of an authentication error.</param>
public sealed record LlmEndpoint(Uri BaseUrl, string ModelId, SecretValue? Key, string? SecretRef);

/// <summary>Retry and timeout of P03 §3.4 and §4.</summary>
public sealed record LlmHttpOptions
{
    /// <summary>Waits before the 2nd, 3rd and 4th send: 20 s, 60 s, 120 s.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];

    /// <summary>Longest wait taken from a <c>Retry-After</c> header.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary><c>llm.request_timeout_seconds</c>: one send.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary><c>User-Agent</c> of every request.</summary>
    public string UserAgent { get; init; } = "arrow-cli/0.0.0";
}
