using System.Text.Json;
using Arrow.Common.Errors;
using Arrow.Common.Json;
using Arrow.Common.Llm;
using Arrow.DataAccess.IO;

namespace Arrow.DataAccess.Llm;

/// <summary>The <c>when</c> of a rule of the fake model (P11 §3.1). Every given field must match; no field matches every call.</summary>
/// <param name="CallId">Exact <c>call_id</c>.</param>
/// <param name="CallIdPrefix"><c>call_id</c> starts with this.</param>
/// <param name="PromptContains">Some message contains this text.</param>
public sealed record FakeRuleWhen(string? CallId, string? CallIdPrefix, string? PromptContains);

/// <summary>Token counts a rule gives to its answer.</summary>
public sealed record FakeUsage(int PromptTokens, int CompletionTokens);

/// <summary>One rule of the fake model.</summary>
/// <param name="When">When the rule applies.</param>
/// <param name="RespondFile">File with the answer, relative to the rules file.</param>
/// <param name="RespondText">The answer itself, instead of a file.</param>
/// <param name="Usage">Tokens of the answer; estimated (characters / 4) when absent.</param>
/// <param name="Error">Simulated failure: <c>429</c>, <c>500</c>, <c>timeout</c> (transient), <c>401</c> (configuration).</param>
/// <param name="Times">With a transient <paramref name="Error"/>: how many sends fail before the answer; without it the error never ends.</param>
/// <param name="FinishReason">Finish reason of the answer; <c>stop</c> by default.</param>
/// <param name="ModelId">Model id of the answer; the model id of the configuration by default.</param>
public sealed record FakeRule(
    FakeRuleWhen? When,
    string? RespondFile,
    string? RespondText,
    FakeUsage? Usage,
    string? Error,
    int? Times,
    string? FinishReason,
    string? ModelId);

/// <summary>The rules file of P11 §3.1.</summary>
/// <param name="Rules">Tried in order; the first match wins.</param>
/// <param name="Default">What to do when no rule matches; <c>{"error": "no_rule"}</c> fails the test.</param>
public sealed record FakeRules(IReadOnlyList<FakeRule> Rules, FakeRule? Default);

/// <summary>
/// A model for tests and E2E runs, answering from a rules file (P11 §3.1); no network, no key. It behaves like a real client on the
/// two points the rest of the product relies on: a transient error is retried inside the call (the answer then has <c>Attempts &gt; 1</c>
/// and <c>LLM.Retry</c> is reported), and a configuration error raises <c>B2_LLM_CONFIG</c>.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly FakeRules _rules;
    private readonly string _directory;
    private readonly string _modelId;
    private readonly Action<LlmRetry>? _onRetry;

    /// <param name="rulesFile">The JSON rules file; <c>respond_file</c> paths are relative to its folder.</param>
    /// <param name="modelId">Model id written into the answers.</param>
    /// <param name="onRetry">Told about every simulated retry.</param>
    public FakeLlmClient(string rulesFile, string modelId, Action<LlmRetry>? onRetry = null)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(rulesFile))!;
        _rules = ArrowJson.Deserialize<FakeRules>(File.ReadAllText(rulesFile));
        _modelId = modelId;
        _onRetry = onRetry;
    }

    /// <summary>Calls answered so far, in order: the <c>call_id</c> of each.</summary>
    public List<string> Calls { get; } = [];

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(request.CallId);
        var rule = _rules.Rules.FirstOrDefault(r => Matches(r.When, request)) ?? _rules.Default;
        if (rule is null || rule.Error == "no_rule")
        {
            var prompt = string.Join("\n", request.Messages.Select(m => m.Content));
            throw new InvalidOperationException($"FakeLlmClient: no rule for call_id '{request.CallId}'. Prompt starts with: {(prompt.Length > 200 ? prompt[..200] : prompt)}");
        }

        var attempts = 1;
        if (rule.Error is { } error)
        {
            if (error == "401")
            {
                throw new ArrowException(ErrorCodes.B2LlmConfig, "HTTP 401: (fake) Khoá API sai hoặc hết hạn.");
            }

            var failures = rule.Times ?? int.MaxValue;
            var sends = new LlmHttpOptions().RetryDelays.Count + 1;
            if (failures >= sends)
            {
                for (var attempt = 1; attempt < sends; attempt++)
                {
                    _onRetry?.Invoke(new LlmRetry(request.CallId, attempt, TimeSpan.Zero, Reason(error)));
                }

                throw new ArrowException(ErrorCodes.B2LlmUnavailable, $"{request.CallId}: không gọi được model sau {sends} lần gửi ({Reason(error)}, giả lập).");
            }

            for (var attempt = 1; attempt <= failures; attempt++)
            {
                _onRetry?.Invoke(new LlmRetry(request.CallId, attempt, TimeSpan.Zero, Reason(error)));
            }

            attempts += failures;
        }

        var content = rule.RespondFile is { } file
            ? File.ReadAllText(Path.Combine(_directory, file))
            : rule.RespondText ?? "";
        var promptTokens = rule.Usage?.PromptTokens ?? EstimateTokens(request.Messages.Sum(m => m.Content.Length));
        var completionTokens = rule.Usage?.CompletionTokens ?? EstimateTokens(content.Length);
        var raw = JsonSerializer.SerializeToElement(new { fake = true, call_id = request.CallId });
        return Task.FromResult(new LlmResponse(
            request.CallId, content, rule.FinishReason ?? "stop", rule.ModelId ?? _modelId, promptTokens, completionTokens,
            rule.Usage is null, 0, attempts, false, null, raw));
    }

    private static bool Matches(FakeRuleWhen? when, LlmRequest request) =>
        when is null
        || ((when.CallId is null || when.CallId == request.CallId)
            && (when.CallIdPrefix is null || request.CallId.StartsWith(when.CallIdPrefix, StringComparison.Ordinal))
            && (when.PromptContains is null || request.Messages.Any(m => m.Content.Contains(when.PromptContains, StringComparison.Ordinal))));

    private static string Reason(string error) => error == "timeout" ? "timeout" : "HTTP " + error;

    private static int EstimateTokens(int characters) => (characters + 3) / 4;
}
