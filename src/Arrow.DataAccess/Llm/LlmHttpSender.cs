using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Arrow.Common.Errors;
using Arrow.Common.Logging;
using Arrow.DataAccess.Security;

namespace Arrow.DataAccess.Llm;

/// <summary>A retry that is about to happen, for the <c>LLM.Retry</c> event (P03 §12).</summary>
/// <param name="CallId">The call.</param>
/// <param name="Attempt">The send that failed (1 for the first).</param>
/// <param name="Wait">How long the sender waits before the next send.</param>
/// <param name="Reason">Short reason, e.g. <c>HTTP 429</c> or <c>timeout</c>.</param>
public sealed record LlmRetry(string CallId, int Attempt, TimeSpan Wait, string Reason);

/// <summary>
/// Sends one request with the error handling of P03 §4, shared by the provider clients: transient errors (429, 500, 502, 503, 504,
/// 529, network errors, timeouts, a body that cannot be read) are retried after 20 s, 60 s and 120 s, or after <c>Retry-After</c>;
/// configuration errors (400, 401, 402, 403, 404, a message about quota or billing) stop at once with <c>B2_LLM_CONFIG</c>.
/// Waiting and timeouts use <see cref="TimeProvider"/>, so tests do not wait.
/// </summary>
internal sealed class LlmHttpSender(HttpClient http, LlmHttpOptions options, TimeProvider time, IAppLog? log, Action<LlmRetry>? onRetry)
{
    private static readonly HashSet<int> TransientStatuses = [408, 429, 500, 502, 503, 504, 529];

    /// <summary>
    /// Sends the request made by <paramref name="build"/> (called again for every send) until it succeeds, a configuration error
    /// stops it, or the retries run out (<c>B2_LLM_UNAVAILABLE</c>).
    /// </summary>
    /// <param name="callId">The call, for logs and events.</param>
    /// <param name="build">Makes the request; called again for every send.</param>
    /// <param name="ct">Cancels the send and the waits.</param>
    /// <param name="parse">Reads a 200 body; returns null when it is not a valid answer (a transient error).</param>
    /// <param name="secretRef">Name of the key, for the hint of a 401 or 403.</param>
    public async Task<(T Value, int Attempts, double Seconds)> SendAsync<T>(
        string callId, Func<HttpRequestMessage> build, Func<string, T?> parse, string? secretRef, CancellationToken ct)
        where T : class
    {
        var failure = "";
        var attempts = options.RetryDelays.Count + 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var request = build();
            request.Headers.UserAgent.ParseAdd(options.UserAgent);
            using var timeout = new CancellationTokenSource(options.RequestTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var watch = Stopwatch.StartNew();
            TimeSpan? retryAfter = null;
            try
            {
                using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    if (parse(text) is { } value)
                    {
                        return (value, attempt, watch.Elapsed.TotalSeconds);
                    }

                    failure = "the body is not a valid answer";
                    Log(callId, attempt, "HTTP " + status + " " + failure);
                }
                else
                {
                    var message = ErrorMessage(text);
                    if (IsConfiguration(status, message))
                    {
                        throw ConfigError(status, message, secretRef);
                    }

                    failure = $"HTTP {status}: {message}";
                    retryAfter = RetryAfter(response);
                    Log(callId, attempt, failure);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = $"timeout after {options.RequestTimeout.TotalSeconds:0} s";
                Log(callId, attempt, failure);
            }
            catch (HttpRequestException ex)
            {
                failure = "network error: " + Redactor.Redact(ex.Message);
                Log(callId, attempt, failure);
            }

            if (attempt < attempts)
            {
                var wait = retryAfter is { } header && header <= options.MaxRetryAfter ? header : options.RetryDelays[attempt - 1];
                onRetry?.Invoke(new LlmRetry(callId, attempt, wait, ShortReason(failure)));
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
            }
        }

        throw new ArrowException(ErrorCodes.B2LlmUnavailable, $"{callId}: không gọi được model sau {attempts} lần gửi ({failure}).");
    }

    /// <summary>
    /// A status or message that retrying cannot fix (P03 §4): 400, 401, 402, 403, 404 and the other 4xx except 408 and 429, and
    /// any answer that talks about quota or billing, 429 included (OpenAI answers an empty account with 429).
    /// </summary>
    internal static bool IsConfiguration(int status, string message) =>
        status is 400 or 401 or 402 or 403 or 404
        || message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
        || message.Contains("billing", StringComparison.OrdinalIgnoreCase)
        || (status is >= 400 and < 500 && !TransientStatuses.Contains(status));

    private static ArrowException ConfigError(int status, string message, string? secretRef)
    {
        var guidance = status switch
        {
            401 or 403 => secretRef is null
                ? " Khoá API sai hoặc hết hạn."
                : $" Khoá API sai hoặc hết hạn: arrow secrets set {secretRef}",
            404 => " Kiểm tra base_url và model_id.",
            _ when status == 402 || message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) || message.Contains("billing", StringComparison.OrdinalIgnoreCase)
                => " Tài khoản hết tiền hoặc hết hạn mức.",
            400 => " Nếu provider từ chối một tham số: arrow config unset llm.models.<tên>.params.<khoá>",
            _ => "",
        };
        return new ArrowException(ErrorCodes.B2LlmConfig, $"HTTP {status}: {message}.{guidance}".Replace("..", ".", StringComparison.Ordinal));
    }

    /// <summary>The message of an error body (<c>error.message</c> or <c>error</c>), or its first 300 characters, redacted.</summary>
    internal static string ErrorMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.ToString() : error.ToString();
                return Redactor.Redact(message.Length > 300 ? message[..300] : message);
            }
        }
        catch (JsonException)
        {
            // Not JSON: show the start of the text.
        }

        var plain = text.Trim();
        return Redactor.Redact(plain.Length > 300 ? plain[..300] : plain);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private void Log(string callId, int attempt, string text)
    {
        var line = Redactor.Redact(text.Length > 200 ? text[..200] : text);
        log?.Warning(string.Create(CultureInfo.InvariantCulture, $"[llm] {callId}: attempt {attempt}: {line}"));
    }

    private static string ShortReason(string failure) =>
        failure.StartsWith("HTTP ", StringComparison.Ordinal) ? failure.Split(':')[0]
        : failure.StartsWith("timeout", StringComparison.Ordinal) ? "timeout"
        : failure.StartsWith("network", StringComparison.Ordinal) ? "lỗi mạng"
        : "câu trả lời không đọc được";
}
