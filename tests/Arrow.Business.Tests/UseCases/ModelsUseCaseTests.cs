using System.Net;
using System.Text;
using System.Text.Json;
using Arrow.Business.Llm;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Llm;
using Arrow.Common.Secrets;
using Arrow.DataAccess.Llm;
using Arrow.DataAccess.Security;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.UseCases;

/// <summary><c>arrow models</c> and the model lines of <c>arrow doctor</c> (P03 §8, §10; P08 §2.5, §2.6).</summary>
public sealed class ModelsUseCaseTests
{
    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, SecretValue> Stored { get; } = [];

        public SecretValue? Get(string reference) => Stored.GetValueOrDefault(reference);

        public void Set(string reference, SecretValue value) => Stored[reference] = value;

        public bool Remove(string reference) => Stored.Remove(reference);

        public IReadOnlyList<SecretInfo> List() => [];
    }

    private sealed class ScriptedFactory(Func<LlmRequest, LlmResponse> answer) : ILlmClientFactory
    {
        public ResolvedModel? Model { get; private set; }

        public LlmRequest? Request { get; private set; }

        public bool? Retry { get; private set; }

        public SecretValue? Key { get; private set; }

        public Exception? Fails { get; set; }

        public ILlmClient Create(ResolvedModel model, RunHandle run, LlmLimits limits, IProgress<ArrowEvent>? progress = null) => throw new NotSupportedException();

        public ILlmClient CreateProvider(ResolvedModel model, LlmLimits limits, IProgress<ArrowEvent>? progress = null, bool retry = true, SecretValue? key = null)
        {
            (Model, Retry, Key) = (model, retry, key);
            return new Client(this);
        }

        private sealed class Client(ScriptedFactory owner) : ILlmClient
        {
            public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
            {
                owner.Request = request;
                return owner.Fails is not null ? throw owner.Fails : Task.FromResult(owner.Answer(request));
            }
        }

        private LlmResponse Answer(LlmRequest request) => answer(request);
    }

    private static LlmResponse Ok(LlmRequest request, string modelId = "gpt-4.1-mini-2025-04-14") =>
        new(request.CallId, "OK", "stop", modelId, 12, 1, false, 0.8, 1, false, null, JsonSerializer.SerializeToElement(new { id = "x" }));

    private sealed class FakeOllamaHandler(string tags, string show) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.RequestUri!.AbsolutePath == "/api/tags" ? tags : show;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Provider(string url, string? secretRef, string kind = "openai-compatible") =>
        JsonSerializer.Serialize(new { kind, base_url = url, secret_ref = secretRef });

    private static (ModelsUseCase UseCase, FakeMachine Machine, MemorySecrets Secrets, ScriptedFactory Factory) Create(
        Func<LlmRequest, LlmResponse>? answer = null, HttpMessageHandler? ollama = null)
    {
        var machine = new FakeMachine();
        var secrets = new MemorySecrets();
        var factory = new ScriptedFactory(answer ?? (r => Ok(r)));
        var useCase = new ModelsUseCase(machine.Config, secrets, factory, new OllamaClient(new HttpClient(ollama ?? new FakeOllamaHandler("{\"models\":[]}", "{}"))));
        return (useCase, machine, secrets, factory);
    }

    private static void Write(FakeMachine machine, string json) => File.WriteAllText(machine.Home.ConfigFile, json);

    private static string Config(string provider, string model, string? defaultModel = "gpt-x") =>
        $$"""{ "schema_version": 1, "llm": { "default_model": {{(defaultModel is null ? "null" : "\"" + defaultModel + "\"")}}, "providers": { "teamo": {{provider}} }, "models": { "gpt-x": {{model}} } } }""";

    private const string DefaultModel = """{ "provider": "teamo", "model_id": "gpt-4.1-mini", "expected_model_id_prefix": "gpt-4.1-mini", "price_usd_per_million": { "input": 0.4, "output": 1.6 } }""";

    // ── List ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ListsModelsWithHostParamsPriceAndTheKey()
    {
        var (useCase, machine, secrets, _) = Create();
        using var _ = machine;
        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), """{ "provider": "teamo", "model_id": "gpt-4.1-mini", "params": { "seed": 7 }, "price_usd_per_million": { "input": 0.4, "output": 1.6 } }"""));
        var listing = useCase.List(null);

        var row = Assert.Single(listing.Rows);
        Assert.Equal(("gpt-x", "teamo", "api.teamorouter.com", "gpt-4.1-mini", "0.4 / 1.6", true), (row.Name, row.Provider, row.Host, row.ModelId, row.Price, row.IsDefault));
        Assert.Equal("temperature=0, max_tokens=16384, seed=7", row.Params);
        Assert.Equal("chưa có khoá", row.Key);

        secrets.Set("teamo", new SecretValue("sk-test-SECRET-123"));
        Assert.Equal("...-123", useCase.List(null).Rows[0].Key.Replace("...", "...", StringComparison.Ordinal));
        Assert.DoesNotContain("SECRET", useCase.List(null).Rows[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderWithoutAKeyShowsNoKeyNeeded()
    {
        var (useCase, machine, _, _) = Create();
        using var _ = machine;
        Write(machine, Config(Provider("http://localhost:11434/v1", null), """{ "provider": "teamo", "model_id": "qwen3-coder:30b" }"""));
        Assert.Equal("không cần", useCase.List(null).Rows[0].Key);
        Assert.Null(useCase.List(null).Rows[0].Price);
    }

    // ── Test ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestSendsTheFixedPromptWithSixteenTokensAndNoRetry()
    {
        var (useCase, machine, _, factory) = Create();
        using var _ = machine;
        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), DefaultModel));

        var result = await useCase.TestAsync(null, null, TestContext.Current.CancellationToken);

        Assert.Equal("Reply with exactly: OK", Assert.Single(factory.Request!.Messages).Content);
        Assert.Equal(16, factory.Request.Params["max_tokens"].GetInt32());
        Assert.Equal(0, factory.Request.Params["temperature"].GetInt32());
        Assert.False(factory.Retry);
        Assert.Equal(("gpt-x", "api.teamorouter.com", "OK", "gpt-4.1-mini-2025-04-14", true), (result.Model, result.ProviderHost, result.Content, result.ModelId, result.PrefixMatches));
        Assert.Equal((12, 1, 0.8), (result.PromptTokens, result.CompletionTokens, result.Seconds));
        Assert.Equal(Math.Round((12 * 0.4 / 1e6) + (1 * 1.6 / 1e6), 8), result.CostUsd);
    }

    [Fact]
    public async Task AWrongModelIdIsReportedNotThrown()
    {
        var (useCase, machine, _, _) = Create(r => Ok(r, "gpt-3.5-turbo"));
        using var _ = machine;
        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), DefaultModel));
        var result = await useCase.TestAsync("gpt-x", null, TestContext.Current.CancellationToken);
        Assert.Equal((false, "gpt-4.1-mini"), (result.PrefixMatches, result.ExpectedPrefix));
    }

    [Fact]
    public async Task NoPrefixMeansNoMatchResultAndNoPriceMeansNoCost()
    {
        var (useCase, machine, _, _) = Create();
        using var _ = machine;
        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), """{ "provider": "teamo", "model_id": "gpt-4.1-mini" }"""));
        var result = await useCase.TestAsync(null, null, TestContext.Current.CancellationToken);
        Assert.Equal((null, null, null), (result.ExpectedPrefix, result.PrefixMatches, result.CostUsd));
    }

    [Fact]
    public async Task TestErrorsKeepTheirCodes()
    {
        var (useCase, machine, _, factory) = Create();
        using var _ = machine;
        Assert.Equal(ErrorCodes.CfgNoModel, (await Assert.ThrowsAsync<ArrowException>(() => useCase.TestAsync(null, null, TestContext.Current.CancellationToken))).Code);
        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), DefaultModel));
        factory.Fails = new ArrowException(ErrorCodes.B2LlmConfig, "HTTP 401: bad key");
        Assert.Equal(ErrorCodes.B2LlmConfig, (await Assert.ThrowsAsync<ArrowException>(() => useCase.TestAsync(null, null, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(ErrorCodes.CfgNoModel, (await Assert.ThrowsAsync<ArrowException>(() => useCase.TestAsync("nope", null, TestContext.Current.CancellationToken))).Code);
    }

    // ── Save and default ────────────────────────────────────────────────────────

    private static ModelDraft Draft(string? key = "sk-test-SECRET-123", bool makeDefault = true) => new(
        "teamo", ProviderKind.OpenAiCompatible, "https://api.teamorouter.com/v1", "teamo", key is null ? null : new SecretValue(key),
        "gpt-x", "gpt-4.1-mini", new ModelPrice(0.4, 1.6), "gpt-4.1-mini", makeDefault);

    [Fact]
    public void SaveWritesTheProviderTheModelTheDefaultAndTheKey()
    {
        var (useCase, machine, secrets, _) = Create();
        using var _ = machine;
        useCase.Save(Draft(), null);

        var settings = machine.Config.Load(null, new Dictionary<string, string?>()).Config;
        Assert.Equal("gpt-x", settings.Llm.DefaultModel);
        var provider = settings.Llm.Providers["teamo"];
        Assert.Equal((ProviderKind.OpenAiCompatible, "https://api.teamorouter.com/v1", "teamo"), (provider.Kind, provider.BaseUrl, provider.SecretRef));
        var model = settings.Llm.Models["gpt-x"];
        Assert.Equal(("teamo", "gpt-4.1-mini", "gpt-4.1-mini", new ModelPrice(0.4, 1.6)), (model.Provider, model.ModelId, model.ExpectedModelIdPrefix, model.PriceUsdPerMillion));
        Assert.Equal("sk-test-SECRET-123", secrets.Stored["teamo"].Reveal());
        // The key is in the secret store, never in config.json.
        Assert.DoesNotContain("SECRET", File.ReadAllText(machine.Home.ConfigFile), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveWithoutAKeyKeepsTheStoredOneAndOllamaNeedsNone()
    {
        var (useCase, machine, secrets, _) = Create();
        using var _ = machine;
        secrets.Set("teamo", new SecretValue("sk-existing-key-1"));
        useCase.Save(Draft(key: null, makeDefault: false), null);
        Assert.Equal("sk-existing-key-1", secrets.Stored["teamo"].Reveal());
        Assert.Null(machine.Config.Load(null, new Dictionary<string, string?>()).Config.Llm.DefaultModel);

        useCase.Save(new ModelDraft("ollama", ProviderKind.OpenAiCompatible, "http://localhost:11434/v1", null, null, "qwen", "qwen3-coder:30b", null, null, true), null);
        var settings = machine.Config.Load(null, new Dictionary<string, string?>()).Config;
        Assert.Null(settings.Llm.Providers["ollama"].SecretRef);
        Assert.Equal("qwen", settings.Llm.DefaultModel);
    }

    [Theory]
    [InlineData("teamo", "gpt-4.1", "gpt-4.1-mini", "teamo", "https://x/v1")]
    [InlineData("teamo", "gpt-x", "", "teamo", "https://x/v1")]
    [InlineData("teamo", "gpt-x", "m", "Bad Ref", "https://x/v1")]
    [InlineData("teamo", "gpt-x", "m", "teamo", "ftp://x")]
    [InlineData("bad.name", "gpt-x", "m", "teamo", "https://x/v1")]
    public void SaveRefusesBadNamesAndUrlsWithoutWritingAnything(string provider, string model, string modelId, string secretRef, string url)
    {
        var (useCase, machine, secrets, _) = Create();
        using var _ = machine;
        var draft = new ModelDraft(provider, ProviderKind.OpenAiCompatible, url, secretRef, new SecretValue("sk-test-SECRET-123"), model, modelId, null, null, true);

        Assert.Equal(ErrorCodes.CfgInvalid, Assert.Throws<ArrowException>(() => useCase.Save(draft, null)).Code);
        Assert.False(File.Exists(machine.Home.ConfigFile));
        Assert.Empty(secrets.Stored);
    }

    [Fact]
    public void DefaultMustBeAConfiguredModel()
    {
        var (useCase, machine, _, _) = Create();
        using var _ = machine;
        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), DefaultModel, defaultModel: null));
        Assert.Equal(ErrorCodes.CfgInvalid, Assert.Throws<ArrowException>(() => useCase.SetDefault("nope", null)).Code);
        useCase.SetDefault("gpt-x", null);
        Assert.Equal("gpt-x", useCase.List(null).DefaultModel);
    }

    [Fact]
    public async Task TestDraftUsesTheKeyOfTheDraftBeforeItIsSaved()
    {
        var (useCase, machine, secrets, factory) = Create();
        using var _ = machine;
        var result = await useCase.TestDraftAsync(Draft(), TestContext.Current.CancellationToken);
        Assert.Equal("OK", result.Content);
        Assert.Equal("sk-test-SECRET-123", factory.Key!.Reveal());
        Assert.Equal((false, 16), (factory.Retry!.Value, factory.Request!.Params["max_tokens"].GetInt32()));
        Assert.Empty(secrets.Stored);
        Assert.False(File.Exists(machine.Home.ConfigFile));
    }

    // ── doctor ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DoctorChecksTheDefaultModel()
    {
        var (useCase, machine, _, _) = Create();
        using var _ = machine;
        Assert.Equal(DoctorLevel.Warning, Assert.Single(await useCase.CheckAsync(null, TestContext.Current.CancellationToken)).Level);

        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), DefaultModel));
        var ok = Assert.Single(await useCase.CheckAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal((DoctorLevel.Ok, "Model gpt-x", "api.teamorouter.com · gpt-4.1-mini-2025-04-14 · 0.8 s"), (ok.Level, ok.Name, ok.Detail));
    }

    [Fact]
    public async Task DoctorReportsAWrongModelAndErrorsWithTheirHints()
    {
        var (useCase, machine, _, factory) = Create(r => Ok(r, "gpt-3.5-turbo"));
        using var _ = machine;
        Write(machine, Config(Provider("https://api.teamorouter.com/v1", "teamo"), DefaultModel));
        var wrong = Assert.Single(await useCase.CheckAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(DoctorLevel.Error, wrong.Level);
        Assert.Contains("gpt-3.5-turbo", wrong.Detail, StringComparison.Ordinal);

        factory.Fails = new ArrowException(ErrorCodes.CfgSecretMissing, args: new Dictionary<string, string> { ["provider"] = "teamo", ["ref"] = "teamo", ["env_var"] = "ARROW_SECRET_TEAMO" });
        var missing = Assert.Single(await useCase.CheckAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(DoctorLevel.Error, missing.Level);
        Assert.Contains("arrow secrets set teamo", missing.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorSkipsTheModelWhenTheConfigurationIsBroken()
    {
        var (useCase, machine, _, _) = Create();
        using var _ = machine;
        Write(machine, "{ not json");
        Assert.Empty(await useCase.CheckAsync(null, TestContext.Current.CancellationToken));
    }

    private const string OllamaConfig = """{ "provider": "teamo", "model_id": "qwen3-coder:30b" }""";

    // P03 §8: Ollama must run, have the model, and a num_ctx of at least 40,960.
    [Fact]
    public async Task OllamaChecksRunningModelAndContextWindow()
    {
        var small = new FakeOllamaHandler("""{"models":[{"name":"qwen3-coder:30b"}]}""", """{"parameters":"num_ctx 4096"}""");
        var (useCase, machine, _, _) = Create(ollama: small);
        using var _ = machine;
        Write(machine, Config(Provider("http://localhost:11434/v1", null), OllamaConfig));

        var items = await useCase.CheckAsync(null, TestContext.Current.CancellationToken);
        var warning = items[0];
        Assert.Equal((DoctorLevel.Warning, "Ollama"), (warning.Level, warning.Name));
        Assert.Contains("4096", warning.Detail, StringComparison.Ordinal);
        Assert.Contains("PARAMETER num_ctx 40960", warning.Hint, StringComparison.Ordinal);
        Assert.Contains("OLLAMA_CONTEXT_LENGTH=40960", warning.Hint, StringComparison.Ordinal);
        Assert.Equal(DoctorLevel.Ok, items[1].Level);

        var large = new FakeOllamaHandler("""{"models":[{"name":"qwen3-coder:30b"}]}""", """{"parameters":"num_ctx 40960"}""");
        var (fine, machine2, _, _) = Create(ollama: large);
        using var _2 = machine2;
        Write(machine2, Config(Provider("http://localhost:11434/v1", null), OllamaConfig));
        Assert.Equal((DoctorLevel.Ok, "num_ctx 40960"), ((await fine.CheckAsync(null, TestContext.Current.CancellationToken))[0].Level, (await fine.CheckAsync(null, TestContext.Current.CancellationToken))[0].Detail));
    }

    [Fact]
    public async Task OllamaWithoutTheModelOrNotRunningStopsBeforeTheCall()
    {
        var missing = new FakeOllamaHandler("""{"models":[{"name":"other:latest"}]}""", "{}");
        var (useCase, machine, _, factory) = Create(ollama: missing);
        using var _ = machine;
        Write(machine, Config(Provider("http://localhost:11434/v1", null), OllamaConfig));

        var item = Assert.Single(await useCase.CheckAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal((DoctorLevel.Error, "ollama pull qwen3-coder:30b"), (item.Level, item.Hint));
        Assert.Null(factory.Request);
    }
}
