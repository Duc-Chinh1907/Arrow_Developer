using System.Text.Json;
using Arrow.Business.Llm;
using Arrow.Common;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Json;
using Arrow.Common.Llm;
using Arrow.DataAccess.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Arrow.Business.Tests.Llm;

/// <summary><c>ReplayingLlmClient</c> (P03 §5, §13), model resolution and cost estimates (P03 §2, §7).</summary>
public sealed class ReplayAndModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-llm-tests", Guid.NewGuid().ToString("N")[..10]);
    private readonly RunStore _runs;
    private readonly RunHandle _run;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));

    public ReplayAndModelTests()
    {
        var home = new ArrowHome(_root);
        home.EnsureCreated();
        _runs = new RunStore(home);
        var info = new RunInfo("20261113-100000-abcd", "test", DateTimeOffset.UtcNow, null, RunStatus.Running, ["gen"],
            new RunProject(ProjectSourceKind.LocalDirectory, "p", null, null, null, null, null, null), null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), [], RunTotals.Zero);
        _run = _runs.Create(info);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static LlmRequest Request(string callId, params (string Name, object Value)[] parameters)
    {
        var map = new Dictionary<string, JsonElement>();
        foreach (var (name, value) in parameters)
        {
            map[name] = JsonSerializer.SerializeToElement(value);
        }

        return new LlmRequest(callId, "gpt-x", [new LlmMessage("system", "sys"), new LlmMessage("user", "same prompt")], map);
    }

    private ReplayingLlmClient Replaying(ILlmClient inner, ResolvedModel? model = null, IProgress<ArrowEvent>? progress = null) =>
        new(inner, model ?? LlmFixtures.Model(), _runs, _run, progress, _time);

    // Parity: Block2Tests.CacheKeyIgnoresParamOrderButSeparatesCallIds (call ids are gen/<cut> and repair/<cut>/round_<k> now)
    [Fact]
    public void KeyIgnoresParameterOrderButSeparatesCallIds()
    {
        var model = LlmFixtures.Model();
        var one = ReplayingLlmClient.CacheKey(Request("gen/a.B", ("temperature", 0), ("max_tokens", 16384)), model);
        var swapped = ReplayingLlmClient.CacheKey(Request("gen/a.B", ("max_tokens", 16384), ("temperature", 0)), model);
        Assert.Equal(one, swapped);
        Assert.Matches("^[0-9a-f]{64}$", one);

        // Two rounds with an identical prompt never share an entry (P03 §5).
        var round1 = ReplayingLlmClient.CacheKey(Request("repair/a.B/round_1", ("temperature", 0)), model);
        var round2 = ReplayingLlmClient.CacheKey(Request("repair/a.B/round_2", ("temperature", 0)), model);
        Assert.NotEqual(round1, round2);
        Assert.NotEqual(one, round1);
    }

    [Fact]
    public void KeyChangesWithModelIdParametersAndMessages()
    {
        var request = Request("gen/a.B", ("temperature", 0));
        var key = ReplayingLlmClient.CacheKey(request, LlmFixtures.Model());
        Assert.NotEqual(key, ReplayingLlmClient.CacheKey(request, LlmFixtures.Model(modelId: "gpt-4.1")));
        Assert.NotEqual(key, ReplayingLlmClient.CacheKey(Request("gen/a.B", ("temperature", 1)), LlmFixtures.Model()));
        Assert.NotEqual(key, ReplayingLlmClient.CacheKey(request with { Messages = [new LlmMessage("user", "other")] }, LlmFixtures.Model()));
        Assert.NotEqual(key, ReplayingLlmClient.CacheKey(request with { Model = "other-name" }, LlmFixtures.Model()));
    }

    [Fact]
    public void NumbersAndNestedObjectsAreCanonical()
    {
        var model = LlmFixtures.Model();
        LlmRequest With(string json) => Request("gen/a.B") with { Params = new Dictionary<string, JsonElement> { ["p"] = JsonDocument.Parse(json).RootElement.Clone() } };
        Assert.Equal(ReplayingLlmClient.CacheKey(With("0"), model), ReplayingLlmClient.CacheKey(With("0.0"), model));
        Assert.Equal(ReplayingLlmClient.CacheKey(With("0.50"), model), ReplayingLlmClient.CacheKey(With("0.5"), model));
        Assert.Equal(ReplayingLlmClient.CacheKey(With("""{"b":1,"a":{"y":2,"x":3}}"""), model), ReplayingLlmClient.CacheKey(With("""{ "a" : {"x":3,"y":2}, "b":1 }"""), model));
        Assert.NotEqual(ReplayingLlmClient.CacheKey(With("1"), model), ReplayingLlmClient.CacheKey(With("\"1\""), model));
    }

    // Parity: Block2Tests.ReplayKeepsOriginalTimeAndCost, RunRepositoryTests.LlmCacheStoresOriginalUsage
    [Fact]
    public async Task ReplayKeepsTheOriginalLatencyTokensCostAndModelId()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, prompt: 2000, completion: 600) with { CostUsd = 0.00538, LatencySeconds = 4.2, Attempts = 2, FinishReason = "length" }));
        var progress = new LlmFixtures.ListProgress();
        var client = Replaying(inner, progress: progress);
        var request = Request("gen/com.myshop.OrderService", ("temperature", 0));

        var real = await client.CompleteAsync(request, ct);
        var replayed = await client.CompleteAsync(request, ct);

        Assert.False(real.Replayed);
        Assert.True(replayed.Replayed);
        Assert.Equal(real with { Replayed = true }, replayed with { RawBody = real.RawBody });
        Assert.Equal((2000, 600, 0.00538, 4.2, 2, "length"), (replayed.PromptTokens, replayed.CompletionTokens, replayed.CostUsd, replayed.LatencySeconds, replayed.Attempts, replayed.FinishReason));
        Assert.Equal(1, inner.Calls);
        var replay = Assert.Single(progress.Events);
        Assert.Equal(ArrowEventTypes.LlmReplayed, replay.Type);

        // The file is under llm_cache/ with the full answer and the time it was stored.
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_run.Root, "llm_cache")));
        var entry = ArrowJson.Deserialize<LlmCacheEntry>(File.ReadAllText(file));
        Assert.Equal((ReplayingLlmClient.CacheKey(request, LlmFixtures.Model()), "gen/com.myshop.OrderService"), (entry.Key, entry.CallId));
        Assert.Equal(_time.GetUtcNow(), entry.StoredAt);
        Assert.Equal(Path.GetFileNameWithoutExtension(file), entry.Key);
        Assert.Contains("\"stored_at\": \"2026-11-13T10:00:00Z\"", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherRoundsModelsAndPromptsCallTheModelAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient();
        var client = Replaying(inner);

        await client.CompleteAsync(Request("repair/a.B/round_1"), ct);
        await client.CompleteAsync(Request("repair/a.B/round_2"), ct);
        Assert.Equal(2, inner.Calls);

        // Another model id: the same call id is a miss (P03 §5 "đổi model … giữa hai lần chạy").
        await Replaying(inner, LlmFixtures.Model(modelId: "gpt-4.1")).CompleteAsync(Request("repair/a.B/round_1"), ct);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task OnlySuccessfulCallsAreStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient((r, n) => n == 1 ? throw new ArrowException(ErrorCodes.B2LlmUnavailable, "down") : Task.FromResult(LlmFixtures.Response(r.CallId)));
        var client = Replaying(inner);
        var request = Request("gen/a.B");

        await Assert.ThrowsAsync<ArrowException>(() => client.CompleteAsync(request, ct));
        Assert.False(Directory.Exists(Path.Combine(_run.Root, "llm_cache")) && Directory.GetFiles(Path.Combine(_run.Root, "llm_cache")).Length > 0);

        Assert.False((await client.CompleteAsync(request, ct)).Replayed);
        Assert.True((await client.CompleteAsync(request, ct)).Replayed);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task ACutCacheFileIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient();
        var client = Replaying(inner);
        var request = Request("gen/a.B");
        _runs.WriteText(_run, $"llm_cache/{ReplayingLlmClient.CacheKey(request, LlmFixtures.Model())}.json", "{ \"key\": \"x\", \"respons");

        var response = await client.CompleteAsync(request, ct);
        Assert.False(response.Replayed);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task AReplayThroughTheGatewayDoesNotCountAgainstTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, prompt: 600, completion: 400)));
        var model = LlmFixtures.Model();
        using var gateway = new LlmGateway(inner, model, new LlmLimits(2, 60, 1500, TimeSpan.FromSeconds(300)), _time);
        var client = Replaying(gateway, model);

        await client.CompleteAsync(Request("gen/a.B"), ct);
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await client.CompleteAsync(Request("gen/a.B"), ct)).Replayed);
        }

        Assert.Equal(1000, gateway.TokensUsed);
        Assert.Equal(1, gateway.Calls);
    }

    // ── ResolvedModel ────────────────────────────────────────────────────────────

    private static ArrowConfig Config(string? defaultModel = "gpt-x", Action<Dictionary<string, ModelSettings>>? models = null)
    {
        var all = new Dictionary<string, ModelSettings>
        {
            ["gpt-x"] = new("teamo", "gpt-4.1-mini", null, new ModelPrice(0.4, 1.6), "gpt-4.1-mini"),
            ["claude"] = new("anthropic", "claude-sonnet-4-5", new Dictionary<string, JsonElement> { ["max_tokens"] = JsonSerializer.SerializeToElement(8000), ["temperature"] = JsonSerializer.SerializeToElement(0.3) }, null, null),
        };
        models?.Invoke(all);
        
        var baseConfig = ArrowDefaults.Config;
        return baseConfig with
        {
            Llm = baseConfig.Llm with
            {
                DefaultModel = defaultModel,
                Models = all,
                Providers = new Dictionary<string, ProviderSettings>
                {
                    ["teamo"] = new(ProviderKind.OpenAiCompatible, "https://api.teamorouter.com/v1", "teamo"),
                    ["anthropic"] = new(ProviderKind.Anthropic, "https://api.anthropic.com", "anthropic"),
                    ["ollama"] = new(ProviderKind.OpenAiCompatible, "http://localhost:11434/v1", null),
                },
            },
        };
    }

    [Fact]
    public void ResolvesTheDefaultModelWithDefaultParameters()
    {
        var model = ResolvedModel.From(Config(), null);
        Assert.Equal(("gpt-x", "teamo", ProviderKind.OpenAiCompatible, "gpt-4.1-mini", "teamo"), (model.Name, model.ProviderName, model.Kind, model.ModelId, model.SecretRef));
        Assert.Equal("api.teamorouter.com", model.ProviderHost);
        // temperature 0 and max_tokens 16384 when the user set neither (P03 §2).
        Assert.Equal((0, 16384), (model.Params["temperature"].GetInt32(), model.MaxTokens));
        Assert.Equal(new ModelPrice(0.4, 1.6), model.Price);
        Assert.Equal("gpt-4.1-mini", model.ExpectedModelIdPrefix);
    }

    [Fact]
    public void KeepsTheParametersOfTheUser()
    {
        var model = ResolvedModel.From(Config(), "claude");
        Assert.Equal((8000, 0.3), (model.MaxTokens, model.Params["temperature"].GetDouble()));
        Assert.Equal(ProviderKind.Anthropic, model.Kind);
    }

    [Fact]
    public void ProviderWithoutAKeyHasNoSecretRef()
    {
        var config = Config(models: m => m["local"] = new ModelSettings("ollama", "qwen3-coder:30b", null, null, null));
        var model = ResolvedModel.From(config, "local");
        Assert.Null(model.SecretRef);
        Assert.Equal("localhost", model.ProviderHost);
    }

    [Fact]
    public void NoModelOrAnUnknownOneIsCfgNoModel()
    {
        Assert.Equal(ErrorCodes.CfgNoModel, Assert.Throws<ArrowException>(() => ResolvedModel.From(Config(defaultModel: null), null)).Code);
        Assert.Contains("nope", Assert.Throws<ArrowException>(() => ResolvedModel.From(Config(), "nope")).Message, StringComparison.Ordinal);
        var orphan = Config(models: m => m["orphan"] = new ModelSettings("missing", "x", null, null, null));
        Assert.Contains("missing", Assert.Throws<ArrowException>(() => ResolvedModel.From(orphan, "orphan")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LimitsComeFromTheConfigurationAndTheCommandLine()
    {
        var config = Config();
        Assert.Equal(new LlmLimits(2, 60, null, TimeSpan.FromSeconds(300)), LlmLimits.From(config));
        Assert.Equal(50_000, LlmLimits.From(config, 50_000).MaxTokens);
    }

    // ── Estimates (P03 §7) ───────────────────────────────────────────────────────

    [Fact]
    public void EstimatesExpectedAndMaximumTokensAndCost()
    {
        var model = LlmFixtures.Model(price: new ModelPrice(1.0, 4.0));
        var estimate = new LlmCostEstimator().Estimate(model, [new PromptSizeHint(12_000), new PromptSizeHint(8_000)], rounds: 5);

        // Per class: generation = chars / 4; repair = generation + 2000; completion = min(16384, 3000) = 3000.
        // Expected = (gen + 3000) + 3 × (gen + 2000 + 3000); maximum = (gen + 3000) + 5 × (…).
        long Expected(long gen) => gen + 3000 + (3 * (gen + 2000 + 3000));
        long Maximum(long gen) => gen + 3000 + (5 * (gen + 2000 + 3000));
        Assert.Equal(Expected(3000) + Expected(2000), estimate.ExpectedTokens);
        Assert.Equal(Maximum(3000) + Maximum(2000), estimate.MaxTokens);
        var expectedIn = (3000 + (3 * 5000)) + (2000 + (3 * 4000));
        var expectedOut = 2 * 4 * 3000;
        Assert.Equal(Math.Round((expectedIn * 1.0 / 1e6) + (expectedOut * 4.0 / 1e6), 4), estimate.ExpectedCostUsd);
        Assert.True(estimate.MaxCostUsd > estimate.ExpectedCostUsd);
    }

    [Fact]
    public void EstimateWithFewRoundsOrNoPrice()
    {
        var estimator = new LlmCostEstimator();
        var noPrice = estimator.Estimate(LlmFixtures.Model(), [new PromptSizeHint(4000)], rounds: 1);
        Assert.Null(noPrice.ExpectedCostUsd);
        Assert.Null(noPrice.MaxCostUsd);
        // N = 1: expected and maximum both have one repair.
        Assert.Equal(noPrice.ExpectedTokens, noPrice.MaxTokens);
        // --no-repair (N = 0): one generation only.
        Assert.Equal(1000 + 3000, estimator.Estimate(LlmFixtures.Model(), [new PromptSizeHint(4000)], rounds: 0).MaxTokens);
        // A small max_tokens lowers the completion estimate.
        var small = LlmFixtures.Model() with { Params = new Dictionary<string, JsonElement> { ["max_tokens"] = JsonSerializer.SerializeToElement(500) } };
        Assert.Equal(1000 + 500, estimator.Estimate(small, [new PromptSizeHint(4000)], rounds: 0).MaxTokens);
    }

    [Fact]
    public void UsageRecordHasTheHostNotTheUrl()
    {
        var usage = LlmUsageRecord.From(LlmFixtures.Response() with { CostUsd = 0.005, Attempts = 2 }, LlmFixtures.Model());
        var json = ArrowJson.Serialize(usage);
        Assert.Contains("\"provider_host\": \"api.teamorouter.com\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cost_usd\": 0.005", json, StringComparison.Ordinal);
        Assert.Contains("\"usage_estimated\": false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", json, StringComparison.Ordinal);
    }
}
