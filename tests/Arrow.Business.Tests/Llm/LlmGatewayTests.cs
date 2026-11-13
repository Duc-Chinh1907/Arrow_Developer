using System.Text.Json;
using Arrow.Business.Llm;
using Arrow.Common;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Llm;
using Microsoft.Extensions.Time.Testing;

namespace Arrow.Business.Tests.Llm;

/// <summary>Shared helpers for the LLM tests of Business.</summary>
internal static class LlmFixtures
{
    public static ResolvedModel Model(string? prefix = null, ModelPrice? price = null, string modelId = "gpt-4.1-mini") => new(
        "gpt-x", "teamo", ProviderKind.OpenAiCompatible, new Uri("https://api.teamorouter.com/v1"), "teamo", modelId,
        new Dictionary<string, JsonElement> { ["temperature"] = JsonSerializer.SerializeToElement(0), ["max_tokens"] = JsonSerializer.SerializeToElement(16384) },
        price, prefix);

    public static LlmRequest Request(string callId = "gen/com.acme.Order", string content = "Test class Order.") => new(
        callId, "gpt-x", [new LlmMessage("system", "sys"), new LlmMessage("user", content)],
        new Dictionary<string, JsonElement> { ["temperature"] = JsonSerializer.SerializeToElement(0), ["max_tokens"] = JsonSerializer.SerializeToElement(16384) });

    public static LlmResponse Response(string callId = "gen/com.acme.Order", string modelId = "gpt-4.1-mini-2025-04-14", int prompt = 100, int completion = 50) => new(
        callId, "class A {}", "stop", modelId, prompt, completion, false, 1.5, 1, false, null, JsonSerializer.SerializeToElement(new { id = "x" }));

    public static LlmResponse Response(int prompt, int completion) => Response(prompt: prompt, completion: completion);

    /// <summary>A scripted inner client; records how many calls ran at the same time.</summary>
    public sealed class ScriptedClient(Func<LlmRequest, int, Task<LlmResponse>>? script = null) : ILlmClient
    {
        private int _running;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxRunning { get; private set; }

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref _calls);
            lock (this)
            {
                MaxRunning = Math.Max(MaxRunning, ++_running);
            }

            try
            {
                return script is null ? Response(request.CallId) : await script(request, number);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    public sealed class ListProgress : IProgress<ArrowEvent>
    {
        public List<ArrowEvent> Events { get; } = [];

        public void Report(ArrowEvent value)
        {
            lock (Events)
            {
                Events.Add(value);
            }
        }
    }
}

/// <summary>
/// <c>LlmGateway</c> (P03 §6, §13). Parity (rewritten from <c>Block2Tests.GatewayComputesCostChecksModelAndLimitsCalls</c>,
/// <c>ModelPrefixMatchesProviderPaths</c> and <c>GatewayWaitsWhenRateLimitIsReached</c>): the limit on calls is now the token budget
/// (<c>B2_TOKEN_BUDGET</c>), cost is null without a price, and a configuration error stops the run.
/// </summary>
public class LlmGatewayTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));

    private static LlmLimits Limits(int concurrent = 2, int rpm = 60, long? budget = null) => new(concurrent, rpm, budget, TimeSpan.FromSeconds(300));

    [Fact]
    public async Task ComputesCostFromThePrice()
    {
        using var gateway = new LlmGateway(new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, prompt: 6120, completion: 1830))),
            LlmFixtures.Model(price: new ModelPrice(0.4, 1.6)), Limits(), _time);
        var response = await gateway.CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(Math.Round((6120 * 0.4 / 1e6) + (1830 * 1.6 / 1e6), 8), response.CostUsd);
        Assert.Equal(7950, gateway.TokensUsed);
        Assert.Equal(1, gateway.Calls);

        // No price: null, never 0.
        using var free = new LlmGateway(new LlmFixtures.ScriptedClient(), LlmFixtures.Model(), Limits(), _time);
        Assert.Null((await free.CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken)).CostUsd);
    }

    // Parity: Block2Tests.ModelPrefixMatchesProviderPaths
    [Theory]
    [InlineData("gpt-4.1-mini", "gpt-4.1-mini-2025-04-14", true)]
    [InlineData("gpt-4.1-mini", "openai/gpt-4.1-mini-2025-04-14", true)]
    [InlineData("GPT-4.1-MINI", "gpt-4.1-mini", true)]
    [InlineData("gpt-4.1-mini", "gpt-4.1", false)]
    [InlineData("gpt-4.1-mini", "my-gpt-4.1-mini", false)]
    [InlineData("", "anything", true)]
    public void ModelPrefixMatching(string prefix, string actual, bool expected) => Assert.Equal(expected, LlmGateway.ModelMatches(prefix, actual));

    [Fact]
    public async Task AWrongModelStopsTheRun()
    {
        var inner = new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, modelId: "gpt-3.5-turbo")));
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(prefix: "gpt-4.1-mini"), Limits(), _time);

        var error = await Assert.ThrowsAsync<ArrowException>(() => gateway.CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.B2WrongModel, error.Code);
        Assert.Equal(("gpt-3.5-turbo", "gpt-4.1-mini"), (error.Args["actual"], error.Args["expected"]));

        // "Dừng run": the next call fails at once and sends nothing.
        var again = await Assert.ThrowsAsync<ArrowException>(() => gateway.CompleteAsync(LlmFixtures.Request("gen/other"), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.B2WrongModel, again.Code);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task NoPrefixMeansNoCheck()
    {
        var inner = new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, modelId: "whatever")));
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(), _time);
        Assert.Equal("whatever", (await gateway.CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken)).ModelId);
    }

    [Fact]
    public async Task AConfigurationErrorStopsTheRunButAnUnavailableProviderDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient((_, n) => n switch
        {
            1 => throw new ArrowException(ErrorCodes.B2LlmUnavailable, "network"),
            2 => throw new ArrowException(ErrorCodes.B2LlmConfig, "HTTP 401: bad key"),
            _ => Task.FromResult(LlmFixtures.Response()),
        });
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(), _time);

        Assert.Equal(ErrorCodes.B2LlmUnavailable, (await Assert.ThrowsAsync<ArrowException>(() => gateway.CompleteAsync(LlmFixtures.Request("a"), ct))).Code);
        Assert.Null(gateway.StoppedBy);
        Assert.Equal(ErrorCodes.B2LlmConfig, (await Assert.ThrowsAsync<ArrowException>(() => gateway.CompleteAsync(LlmFixtures.Request("b"), ct))).Code);
        var later = await Assert.ThrowsAsync<ArrowException>(() => gateway.CompleteAsync(LlmFixtures.Request("c"), ct));
        Assert.Equal((ErrorCodes.B2LlmConfig, "HTTP 401: bad key"), (later.Code, later.Detail));
        Assert.Equal(2, inner.Calls);
    }

    // P03 §6: the budget is checked before each call; the call that crosses it finishes.
    [Fact]
    public async Task TheTokenBudgetStopsTheRunAfterTheCallThatCrossesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, prompt: 600, completion: 400)));
        var progress = new LlmFixtures.ListProgress();
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(budget: 2500), _time, progress);

        await gateway.CompleteAsync(LlmFixtures.Request("1"), ct);
        await gateway.CompleteAsync(LlmFixtures.Request("2"), ct);
        await gateway.CompleteAsync(LlmFixtures.Request("3"), ct);
        Assert.Equal(3000, gateway.TokensUsed);

        var error = await Assert.ThrowsAsync<ArrowException>(() => gateway.CompleteAsync(LlmFixtures.Request("4"), ct));
        Assert.Equal(ErrorCodes.B2TokenBudget, error.Code);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(ErrorCodes.B2TokenBudget, (await Assert.ThrowsAsync<ArrowException>(() => gateway.CompleteAsync(LlmFixtures.Request("5"), ct))).Code);
    }

    [Fact]
    public async Task WarnsOnceAtEightyPercentOfTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, prompt: 500, completion: 500)));
        var progress = new LlmFixtures.ListProgress();
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(budget: 2500), _time, progress);

        await gateway.CompleteAsync(LlmFixtures.Request("1"), ct);
        Assert.Empty(progress.Events);
        await gateway.CompleteAsync(LlmFixtures.Request("2"), ct);

        // 2000 of 2500 tokens is 80%.
        var warning = Assert.Single(progress.Events);
        Assert.Equal((ArrowEventTypes.LlmBudgetWarning, EventLevel.Warning), (warning.Type, warning.Level));
        Assert.Equal(2000L, warning.Data["tokens_used"]);

        // Only once.
        await gateway.CompleteAsync(LlmFixtures.Request("3"), ct);
        Assert.Single(progress.Events);
    }

    [Fact]
    public async Task NoBudgetMeansNoLimit()
    {
        var inner = new LlmFixtures.ScriptedClient((r, _) => Task.FromResult(LlmFixtures.Response(r.CallId, prompt: 1_000_000, completion: 1_000_000)));
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(), _time);
        for (var i = 0; i < 3; i++)
        {
            await gateway.CompleteAsync(LlmFixtures.Request("c" + i), TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task LimitsTheNumberOfConcurrentCalls()
    {
        var release = new TaskCompletionSource();
        var inner = new LlmFixtures.ScriptedClient(async (r, _) =>
        {
            await release.Task;
            return LlmFixtures.Response(r.CallId);
        });
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(concurrent: 2), _time);

        var calls = Enumerable.Range(0, 5).Select(i => gateway.CompleteAsync(LlmFixtures.Request("c" + i), TestContext.Current.CancellationToken)).ToList();
        while (inner.Calls < 2)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, inner.Calls);
        release.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(5, inner.Calls);
        Assert.Equal(2, inner.MaxRunning);
    }

    // Parity: Block2Tests.GatewayWaitsWhenRateLimitIsReached (sliding window of 60 s)
    [Fact]
    public async Task WaitsWhenTheRateLimitIsReached()
    {
        var inner = new LlmFixtures.ScriptedClient();
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(rpm: 2), _time);
        var ct = TestContext.Current.CancellationToken;

        await gateway.CompleteAsync(LlmFixtures.Request("1"), ct);
        _time.Advance(TimeSpan.FromSeconds(10));
        await gateway.CompleteAsync(LlmFixtures.Request("2"), ct);

        var third = gateway.CompleteAsync(LlmFixtures.Request("3"), ct);
        await Task.Delay(50, ct);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, inner.Calls);

        // 59 s after the first call the window still holds two calls; at 60 s the first one leaves it.
        _time.Advance(TimeSpan.FromSeconds(49));
        await Task.Delay(100, ct);
        Assert.False(third.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1.1));
        while (!third.IsCompleted)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        await third;
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task ACancelledWaitSendsNothing()
    {
        var inner = new LlmFixtures.ScriptedClient();
        using var gateway = new LlmGateway(inner, LlmFixtures.Model(), Limits(rpm: 1), _time);
        await gateway.CompleteAsync(LlmFixtures.Request("1"), TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var second = gateway.CompleteAsync(LlmFixtures.Request("2"), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, inner.Calls);
    }
}
