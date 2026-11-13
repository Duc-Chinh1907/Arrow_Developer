using System.Globalization;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Llm;

namespace Arrow.Business.Llm;

/// <summary>
/// The limits and checks around every real call (P03 §6): concurrent calls, calls per minute (a sliding window of 60 s), the token
/// budget of the run, the model that answered, and the cost. A configuration problem (<c>B2_LLM_CONFIG</c>, <c>B2_WRONG_MODEL</c>) or
/// the end of the budget stops the run: every later call fails at once with the same error and no call is started (P03 §4).
/// </summary>
public sealed class LlmGateway : ILlmClient, IDisposable
{
    private const double WarnAt = 0.8;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(1);

    private readonly ILlmClient _inner;
    private readonly ResolvedModel _model;
    private readonly LlmLimits _limits;
    private readonly TimeProvider _time;
    private readonly IProgress<ArrowEvent>? _progress;
    private readonly SemaphoreSlim _concurrency;
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _gate = new();
    private long _tokens;
    private int _calls;
    private int _warned;
    private ArrowException? _stopped;

    public LlmGateway(ILlmClient inner, ResolvedModel model, LlmLimits limits, TimeProvider? time = null, IProgress<ArrowEvent>? progress = null)
    {
        _inner = inner;
        _model = model;
        _limits = limits;
        _time = time ?? TimeProvider.System;
        _progress = progress;
        _concurrency = new SemaphoreSlim(Math.Max(1, limits.MaxConcurrent));
    }

    /// <summary>Tokens (in + out) of the real calls that finished; replays are not counted.</summary>
    public long TokensUsed => Interlocked.Read(ref _tokens);

    /// <summary>Real calls that were sent.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The error that stopped the run, or null.</summary>
    public ArrowException? StoppedBy => Volatile.Read(ref _stopped);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ThrowIfStopped();
        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForRateAsync(ct).ConfigureAwait(false);

            // After the waits, so that calls queued behind a long call see the tokens it used. A call that is running may finish:
            // the budget can be exceeded by at most one call per concurrent slot.
            ThrowIfStopped();
            if (_limits.MaxTokens is { } budget && TokensUsed >= budget)
            {
                throw Stop(new ArrowException(ErrorCodes.B2TokenBudget, $"Đã dùng {TokensUsed:N0} token, hết ngân sách {budget:N0} token."));
            }

            Interlocked.Increment(ref _calls);
            LlmResponse response;
            try
            {
                response = await _inner.CompleteAsync(request, ct).ConfigureAwait(false);
            }
            catch (ArrowException ex) when (ex.Code == ErrorCodes.B2LlmConfig)
            {
                throw Stop(ex);
            }

            if (_model.ExpectedModelIdPrefix is { Length: > 0 } prefix && !ModelMatches(prefix, response.ModelId))
            {
                throw Stop(new ArrowException(ErrorCodes.B2WrongModel, args: new Dictionary<string, string>
                {
                    ["actual"] = response.ModelId.Length == 0 ? "(không có model id)" : response.ModelId,
                    ["expected"] = prefix,
                }));
            }

            var total = Interlocked.Add(ref _tokens, response.TotalTokens);
            WarnWhenNear(total);
            return response with { CostUsd = Cost(_model.Price, response) };
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// The model id of the answer starts with the prefix, or contains <c>/&lt;prefix&gt;</c> (a provider may put its own path in front),
    /// ignoring case (P03 §6). An empty prefix matches everything.
    /// </summary>
    public static bool ModelMatches(string expectedPrefix, string modelId) =>
        expectedPrefix.Length == 0
        || modelId.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
        || modelId.Contains("/" + expectedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>cost_usd = prompt_tokens × input / 1e6 + completion_tokens × output / 1e6</c>; null (not 0) when the model has no price (P03 §7).
    /// </summary>
    public static double? Cost(Common.Config.ModelPrice? price, LlmResponse response) =>
        price is null ? null : Math.Round((response.PromptTokens * price.Input / 1e6) + (response.CompletionTokens * price.Output / 1e6), 8);

    private void ThrowIfStopped()
    {
        if (StoppedBy is { } stopped)
        {
            throw new ArrowException(stopped.Code, stopped.Detail, stopped.Args);
        }
    }

    private ArrowException Stop(ArrowException error)
    {
        Interlocked.CompareExchange(ref _stopped, error, null);
        return error;
    }

    private void WarnWhenNear(long total)
    {
        if (_limits.MaxTokens is { } budget && total >= budget * WarnAt && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _progress?.Report(new ArrowEvent(
                _time.GetUtcNow(),
                ArrowEventTypes.LlmBudgetWarning,
                null,
                null,
                null,
                EventLevel.Warning,
                string.Create(CultureInfo.InvariantCulture, $"Đã dùng {total:N0} / {budget:N0} token ({total * 100 / budget}% ngân sách)."),
                new Dictionary<string, object?> { ["tokens_used"] = total, ["budget"] = budget }));
        }
    }

    /// <summary>Waits until fewer than <c>rpm_limit</c> calls started in the last 60 s, checking every 50 ms to 1 s.</summary>
    private async Task WaitForRateAsync(CancellationToken ct)
    {
        if (_limits.RpmLimit <= 0)
        {
            return;
        }

        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                while (_recent.Count > 0 && now - _recent.Peek() >= Window)
                {
                    _recent.Dequeue();
                }

                if (_recent.Count < _limits.RpmLimit)
                {
                    _recent.Enqueue(now);
                    return;
                }

                wait = _recent.Peek() + Window - now;
            }

            await Task.Delay(wait < MinWait ? MinWait : wait > MaxWait ? MaxWait : wait, _time, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _concurrency.Dispose();
}
