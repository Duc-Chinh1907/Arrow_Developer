using System.Net;
using System.Text.Json;
using Arrow.Common.Errors;
using Arrow.Common.Llm;
using Arrow.DataAccess.Llm;
using Microsoft.Extensions.Time.Testing;
using static Arrow.DataAccess.Tests.Llm.LlmTestSupport;

namespace Arrow.DataAccess.Tests.Llm;

/// <summary><c>anthropic</c> (P03 §3.3) and the fake model of P11 §3.1.</summary>
public class AnthropicAndFakeClientTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<LlmRetry> _retries = [];

    private AnthropicClient Anthropic(FakeHttp http) =>
        new(new HttpClient(http), Endpoint("https://api.anthropic.com", "claude-sonnet-4-5", FakeKey, "anthropic"), new LlmHttpOptions(), _time, null, _retries.Add);

    private static string MessagesBody(string stopReason = "end_turn", params string[] parts) =>
        JsonSerializer.Serialize(new
        {
            id = "msg_1",
            model = "claude-sonnet-4-5-20250929",
            content = (parts.Length == 0 ? ["OK"] : parts).Select(t => new { type = "text", text = t }).ToArray(),
            stop_reason = stopReason,
            usage = new { input_tokens = 321, output_tokens = 45 },
        });

    [Fact]
    public async Task SendsTheMessagesApiRequest()
    {
        var http = new FakeHttp().Respond(HttpStatusCode.OK, MessagesBody());
        await Drive(Anthropic(http).CompleteAsync(Request(), TestContext.Current.CancellationToken), _time);

        var (sent, body) = Assert.Single(http.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.RequestUri!.AbsoluteUri);
        Assert.Equal(FakeKey, sent.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", sent.Headers.GetValues("anthropic-version").Single());
        Assert.Null(sent.Headers.Authorization);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("claude-sonnet-4-5", json.GetProperty("model").GetString());
        Assert.Equal("You write tests.", json.GetProperty("system").GetString());
        var message = Assert.Single(json.GetProperty("messages").EnumerateArray());
        Assert.Equal(("user", "Test class Order."), (message.GetProperty("role").GetString(), message.GetProperty("content").GetString()));
        Assert.Equal((16384, 0), (json.GetProperty("max_tokens").GetInt32(), json.GetProperty("temperature").GetInt32()));
    }

    [Fact]
    public async Task MaxTokensIsAddedWhenTheParametersHaveNone()
    {
        var request = Request() with { Params = new Dictionary<string, JsonElement> { ["temperature"] = JsonSerializer.SerializeToElement(0.2) } };
        Assert.Equal(16384, JsonDocument.Parse(AnthropicClient.Body(request, "m")).RootElement.GetProperty("max_tokens").GetInt32());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task JoinsTextPartsAndMapsStopReasons()
    {
        async Task<LlmResponse> One(string stop, params string[] parts) =>
            await Drive(Anthropic(new FakeHttp().Respond(HttpStatusCode.OK, MessagesBody(stop, parts))).CompleteAsync(Request(), TestContext.Current.CancellationToken), _time);

        var multi = await One("end_turn", "part one ", "part two");
        Assert.Equal("part one part two", multi.Content);
        Assert.Equal(("stop", "claude-sonnet-4-5-20250929", 321, 45), (multi.FinishReason, multi.ModelId, multi.PromptTokens, multi.CompletionTokens));
        Assert.Equal("stop", (await One("stop_sequence")).FinishReason);
        Assert.Equal("length", (await One("max_tokens")).FinishReason);
        Assert.Equal("refusal", (await One("refusal")).FinishReason);
    }

    [Fact]
    public async Task OverloadedIsRetried()
    {
        var http = new FakeHttp().Respond((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""").Respond(HttpStatusCode.OK, MessagesBody());
        var response = await Drive(Anthropic(http).CompleteAsync(Request(), TestContext.Current.CancellationToken), _time);
        Assert.Equal(2, response.Attempts);
        Assert.Equal([20], _retries.Select(r => (int)r.Wait.TotalSeconds));
    }

    // ── FakeLlmClient ────────────────────────────────────────────────────────────

    private static string Rules(TempDir temp, string json)
    {
        temp.Write("c0.java", "class A_ArrowTest {}");
        return temp.Write("rules.json", json);
    }

    [Fact]
    public async Task FakeAnswersByFirstMatchingRule()
    {
        using var temp = new TempDir();
        var file = Rules(temp, """
            { "rules": [
              { "when": { "call_id": "gen/com.acme.Order" }, "respond_file": "c0.java" },
              { "when": { "call_id_prefix": "repair/com.acme.Order/round_1", "prompt_contains": "cannot find symbol" }, "respond_text": "fixed", "usage": { "prompt_tokens": 2000, "completion_tokens": 600 } },
              { "when": { "call_id_prefix": "repair/" }, "respond_text": "generic", "finish_reason": "length", "model_id": "other-model" }
            ], "default": { "error": "no_rule" } }
            """);
        var fake = new FakeLlmClient(file, "gpt-4.1-mini");
        var ct = TestContext.Current.CancellationToken;

        var gen = await fake.CompleteAsync(Request("gen/com.acme.Order"), ct);
        Assert.Equal(("class A_ArrowTest {}", "stop", "gpt-4.1-mini", true), (gen.Content, gen.FinishReason, gen.ModelId, gen.UsageEstimated));

        var round1 = Request("repair/com.acme.Order/round_1") with { Messages = [new LlmMessage("user", "error: cannot find symbol")] };
        var repaired = await fake.CompleteAsync(round1, ct);
        Assert.Equal(("fixed", 2000, 600, false), (repaired.Content, repaired.PromptTokens, repaired.CompletionTokens, repaired.UsageEstimated));

        // Same prefix without the text of the error: the next rule.
        var generic = await fake.CompleteAsync(Request("repair/com.acme.Order/round_1"), ct);
        Assert.Equal(("generic", "length", "other-model"), (generic.Content, generic.FinishReason, generic.ModelId));
        Assert.Equal(["gen/com.acme.Order", "repair/com.acme.Order/round_1", "repair/com.acme.Order/round_1"], fake.Calls);
    }

    [Fact]
    public async Task FakeFailsLoudlyWhenNoRuleMatches()
    {
        using var temp = new TempDir();
        var fake = new FakeLlmClient(Rules(temp, """{ "rules": [], "default": { "error": "no_rule" } }"""), "m");
        var request = Request("gen/com.acme.Missing") with { Messages = [new LlmMessage("user", new string('p', 500))] };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fake.CompleteAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("gen/com.acme.Missing", error.Message, StringComparison.Ordinal);
        Assert.Contains(new string('p', 200), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('p', 201), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeSimulatesTransientAndConfigurationErrors()
    {
        using var temp = new TempDir();
        var retries = new List<LlmRetry>();
        var fake = new FakeLlmClient(Rules(temp, """
            { "rules": [
              { "when": { "call_id": "a" }, "error": "429", "times": 1, "respond_text": "after retry" },
              { "when": { "call_id": "b" }, "error": "500" },
              { "when": { "call_id": "c" }, "error": "401" },
              { "when": { "call_id": "d" }, "error": "timeout", "times": 2, "respond_text": "late" }
            ] }
            """), "m", retries.Add);
        var ct = TestContext.Current.CancellationToken;

        var a = await fake.CompleteAsync(Request("a"), ct);
        Assert.Equal(("after retry", 2), (a.Content, a.Attempts));
        Assert.Equal(["HTTP 429"], retries.Select(r => r.Reason));

        retries.Clear();
        Assert.Equal(ErrorCodes.B2LlmUnavailable, (await Assert.ThrowsAsync<ArrowException>(() => fake.CompleteAsync(Request("b"), ct))).Code);
        Assert.Equal(3, retries.Count);
        Assert.Equal(ErrorCodes.B2LlmConfig, (await Assert.ThrowsAsync<ArrowException>(() => fake.CompleteAsync(Request("c"), ct))).Code);

        retries.Clear();
        var d = await fake.CompleteAsync(Request("d"), ct);
        Assert.Equal((3, 2), (d.Attempts, retries.Count));
        Assert.All(retries, r => Assert.Equal("timeout", r.Reason));
    }
}
