using Arrow.Common;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Llm;
using Arrow.Common.Logging;
using Arrow.Common.Secrets;
using Arrow.DataAccess.Llm;
using Arrow.DataAccess.Security;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Llm;

/// <summary>Builds the chain of P03 for a run: replay cache, then gateway, then the client of the provider (P03 §12).</summary>
public interface ILlmClientFactory
{
    /// <summary>
    /// The client for <paramref name="model"/> in <paramref name="run"/>. Raises <c>CFG_SECRET_MISSING</c> when the provider needs a key
    /// and none is found. The gateway is created here, so its limits and budget are shared by every call of the run.
    /// </summary>
    ILlmClient Create(ResolvedModel model, RunHandle run, LlmLimits limits, IProgress<ArrowEvent>? progress = null);
}

/// <summary><see cref="ILlmClientFactory"/> with the real HTTP clients, or a fake model when one is given.</summary>
/// <param name="secrets">Where the keys are.</param>
/// <param name="runs">Where the replay cache is written.</param>
/// <param name="userAgent">Version string for <c>User-Agent: arrow-cli/&lt;version&gt;</c>.</param>
/// <param name="log">The tool log.</param>
/// <param name="http">Client shared by the calls; null makes one with <see cref="LlmHttpClientFactory.Create"/>.</param>
/// <param name="time">Clock, for tests.</param>
/// <param name="fakeRulesFile">Rules file of <c>ARROW_FAKE_LLM</c> (P11 §3.2); when set, no provider is called and no key is needed.</param>
public sealed class LlmClientFactory(ISecretStore secrets, IRunStore runs, string userAgent, IAppLog? log = null, HttpClient? http = null, TimeProvider? time = null, string? fakeRulesFile = null) : ILlmClientFactory
{
    private readonly Lazy<HttpClient> _http = new(() => http ?? LlmHttpClientFactory.Create());

    /// <summary>True when the calls go to a fake model (<c>ARROW_FAKE_LLM</c>); the CLI then prints <c>[!] FAKE LLM</c>.</summary>
    public bool IsFake => fakeRulesFile is not null;

    public ILlmClient Create(ResolvedModel model, RunHandle run, LlmLimits limits, IProgress<ArrowEvent>? progress = null)
    {
        void OnRetry(LlmRetry retry) =>
            progress?.Report(new ArrowEvent(
                (time ?? TimeProvider.System).GetUtcNow(),
                ArrowEventTypes.LlmRetry,
                null,
                null,
                null,
                EventLevel.Warning,
                $"gọi lại sau {retry.Wait.TotalSeconds:0} s ({retry.Reason})",
                new Dictionary<string, object?> { ["call_id"] = retry.CallId, ["attempt"] = retry.Attempt, ["wait_seconds"] = retry.Wait.TotalSeconds, ["reason"] = retry.Reason }));

        ILlmClient provider;
        if (fakeRulesFile is not null)
        {
            provider = new FakeLlmClient(fakeRulesFile, model.ModelId, OnRetry);
        }
        else
        {
            var key = Key(model);
            var endpoint = new LlmEndpoint(model.BaseUrl, model.ModelId, key, model.SecretRef);
            var options = new LlmHttpOptions { RequestTimeout = limits.RequestTimeout, UserAgent = userAgent };
            provider = model.Kind == ProviderKind.Anthropic
                ? new AnthropicClient(_http.Value, endpoint, options, time, log, OnRetry)
                : new OpenAiCompatibleClient(_http.Value, endpoint, options, time, log, OnRetry);
        }

        var gateway = new LlmGateway(provider, model, limits, time, progress);
        return new ReplayingLlmClient(gateway, model, runs, run, progress, time);
    }

    /// <summary>The key of the provider of <paramref name="model"/>; null when it needs none. Raises <c>CFG_SECRET_MISSING</c> when it is not found.</summary>
    public SecretValue? Key(ResolvedModel model)
    {
        if (model.SecretRef is null)
        {
            return null;
        }

        var key = secrets.Get(model.SecretRef)
            ?? throw new ArrowException(ErrorCodes.CfgSecretMissing, args: new Dictionary<string, string>
            {
                ["provider"] = model.ProviderName,
                ["ref"] = model.SecretRef,
                ["env_var"] = SecretStore.EnvironmentPrefix + model.SecretRef.ToUpperInvariant().Replace('-', '_'),
            });
        Redactor.Register(key.Reveal());
        return key;
    }
}
