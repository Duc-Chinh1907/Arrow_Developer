using System.Net;
using System.Text.Json;
using Arrow.Common.Errors;
using Arrow.Common.Llm;
using Arrow.DataAccess.Llm;
using Arrow.DataAccess.Security;
using Microsoft.Extensions.Time.Testing;
using static Arrow.DataAccess.Tests.Llm.LlmTestSupport;

namespace Arrow.DataAccess.Tests.Llm;

/// <summary>
/// <c>openai-compatible</c> and the retry rules of P03 §3.2, §4, §13, with a fake HTTP handler and a fake clock.
/// Parity (rewritten from <c>Block2Tests.LiteLlm*</c>): parameters sent as they are, usage parsed, transient errors retried,
/// configuration errors stopped; the number of retries and the codes follow P03 now.
/// </summary>
public class OpenAiCompatibleClientTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ListLog _log = new();
    private readonly List<LlmRetry> _retries = [];

    private OpenAiCompatibleClient Client(FakeHttp http, string? key = FakeKey, LlmHttpOptions? options = null) =>
        new(new HttpClient(http), Endpoint(key: key), options ?? new LlmHttpOptions { UserAgent = "arrow-cli/1.2.3" }, _time, _log, _retries.Add);

    private static string ErrorBody(string message) => JsonSerializer.Serialize(new { error = new { message } });

    private Task<LlmResponse> Call(OpenAiCompatibleClient client, LlmRequest? request = null) =>
        Drive(client.CompleteAsync(request ?? Request(), TestContext.Current.CancellationToken), _time);

    [Fact]
    public async Task SendsTheRequestAsP03Describes()
    {
        var http = new FakeHttp().Respond(HttpStatusCode.OK, OpenAiBody());
        var request = Request(parameters: [("reasoning_effort", "low"), ("seed", 7)]);

        await Call(Client(http), request);

        var (sent, body) = Assert.Single(http.Requests);
        Assert.Equal("https://api.teamorouter.com/v1/chat/completions", sent.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("Bearer " + FakeKey, sent.Headers.Authorization!.ToString());
        Assert.Equal("arrow-cli/1.2.3", sent.Headers.UserAgent.ToString());
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("gpt-4.1-mini", json.GetProperty("model").GetString());
        Assert.Equal("system", json.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Test class Order.", json.GetProperty("messages")[1].GetProperty("content").GetString());
        // The parameters go as they are; none is dropped.
        Assert.Equal(0, json.GetProperty("temperature").GetInt32());
        Assert.Equal(16384, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("low", json.GetProperty("reasoning_effort").GetString());
        Assert.Equal(7, json.GetProperty("seed").GetInt32());
        Assert.False(json.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ReadsContentUsageAndFinishReason()
    {
        var http = new FakeHttp().Respond(HttpStatusCode.OK, OpenAiBody("public class A {}", "openai/gpt-4.1-mini-2025-04-14", "length", 6120, 1830));
        var response = await Call(Client(http));

        Assert.Equal("public class A {}", response.Content);
        Assert.Equal(("length", "openai/gpt-4.1-mini-2025-04-14"), (response.FinishReason, response.ModelId));
        Assert.Equal((6120, 1830, false), (response.PromptTokens, response.CompletionTokens, response.UsageEstimated));
        Assert.Equal((1, false, "gen/com.acme.Order"), (response.Attempts, response.Replayed, response.CallId));
        Assert.Equal("x", response.RawBody.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ContentMayBeAnArrayOfParts()
    {
        const string body = """{"model":"m","choices":[{"message":{"content":[{"type":"text","text":"Hello "},{"type":"image","text":"skip"},{"type":"text","text":"world"}]},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}""";
        var response = await Call(Client(new FakeHttp().Respond(HttpStatusCode.OK, body)));
        Assert.Equal("Hello world", response.Content);
    }

    [Fact]
    public async Task WithoutUsageTheTokensAreEstimated()
    {
        var content = new string('x', 40);
        var body = JsonSerializer.Serialize(new { model = "m", choices = new[] { new { message = new { content }, finish_reason = "stop" } } });
        var response = await Call(Client(new FakeHttp().Respond(HttpStatusCode.OK, body)));

        Assert.True(response.UsageEstimated);
        Assert.Equal(10, response.CompletionTokens);
        Assert.Equal(("You write tests.".Length + "Test class Order.".Length + 3) / 4, response.PromptTokens);
    }

    [Fact]
    public async Task EmptyChoicesAndNullContentAreAnEmptyAnswer()
    {
        var empty = await Call(Client(new FakeHttp().Respond(HttpStatusCode.OK, """{"model":"m","choices":[]}""")));
        Assert.Equal(("", null), (empty.Content, empty.FinishReason));

        var nullContent = await Call(Client(new FakeHttp().Respond(HttpStatusCode.OK, """{"model":"m","choices":[{"message":{"content":null},"finish_reason":"content_filter"}]}""")));
        Assert.Equal(("", "content_filter"), (nullContent.Content, nullContent.FinishReason));
    }

    [Fact]
    public async Task NoKeyMeansNoAuthorizationHeader()
    {
        var http = new FakeHttp().Respond(HttpStatusCode.OK, OpenAiBody());
        var client = new OpenAiCompatibleClient(new HttpClient(http), Endpoint("http://localhost:11434/v1", "qwen3-coder:30b", key: null, secretRef: null), time: _time);
        await Drive(client.CompleteAsync(Request(), TestContext.Current.CancellationToken), _time);
        Assert.Null(http.Requests[0].Request.Headers.Authorization);
        Assert.Equal("http://localhost:11434/v1/chat/completions", http.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    // P03 §4: 500 four times → B2_LLM_UNAVAILABLE after waits of 20, 60 and 120 s.
    [Fact]
    public async Task FourServerErrorsEndAsUnavailableAfterTheThreeWaits()
    {
        var http = new FakeHttp();
        for (var i = 0; i < 4; i++)
        {
            http.Respond(HttpStatusCode.InternalServerError, """{"error":{"message":"overloaded"}}""");
        }

        var error = await Assert.ThrowsAsync<ArrowException>(() => Call(Client(http)));

        Assert.Equal(ErrorCodes.B2LlmUnavailable, error.Code);
        Assert.Equal(4, http.Requests.Count);
        Assert.Equal([20, 60, 120], _retries.Select(r => (int)r.Wait.TotalSeconds));
        Assert.Equal([1, 2, 3], _retries.Select(r => r.Attempt));
        Assert.All(_retries, r => Assert.Equal("HTTP 500", r.Reason));
        Assert.Contains("overloaded", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, _log.Lines.Count(l => l.Contains("attempt", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(529)]
    public async Task TransientStatusesAreRetriedOnce(int status)
    {
        var http = new FakeHttp().Respond((HttpStatusCode)status, "{}").Respond(HttpStatusCode.OK, OpenAiBody());
        var response = await Call(Client(http));
        Assert.Equal(2, response.Attempts);
        Assert.Equal([20], _retries.Select(r => (int)r.Wait.TotalSeconds));
    }

    [Fact]
    public async Task RetryAfterIsUsedUpToFiveMinutes()
    {
        var http = new FakeHttp()
            .Respond(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7)))
            .Respond(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(999)))
            .Respond(HttpStatusCode.OK, OpenAiBody());

        var response = await Call(Client(http));

        Assert.Equal(3, response.Attempts);
        // 7 s from the header; 999 s is over 300 s, so the schedule of P03 §4 (60 s for the second wait) applies.
        Assert.Equal([7, 60], _retries.Select(r => (int)r.Wait.TotalSeconds));
    }

    [Fact]
    public async Task NetworkErrorsAndBrokenBodiesAreRetried()
    {
        var http = new FakeHttp()
            .Fail(new HttpRequestException("Connection reset"))
            .Respond(HttpStatusCode.OK, "<html>not json</html>")
            .Respond(HttpStatusCode.OK, """{"model":"m"}""")
            .Respond(HttpStatusCode.OK, OpenAiBody());

        var response = await Call(Client(http));

        Assert.Equal(4, response.Attempts);
        Assert.Equal(["lỗi mạng", "câu trả lời không đọc được", "câu trả lời không đọc được"], _retries.Select(r => r.Reason));
    }

    [Fact]
    public async Task ATimeoutIsRetried()
    {
        var http = new FakeHttp().Hang().Respond(HttpStatusCode.OK, OpenAiBody());
        var response = await Call(Client(http, options: new LlmHttpOptions { RequestTimeout = TimeSpan.FromSeconds(300) }));
        Assert.Equal(2, response.Attempts);
        Assert.Equal("timeout", _retries.Single().Reason);
    }

    [Theory]
    [InlineData(400, "Unsupported parameter: 'temperature'", "arrow config unset llm.models")]
    [InlineData(401, "Incorrect API key", "arrow secrets set teamo")]
    [InlineData(403, "Forbidden", "arrow secrets set teamo")]
    [InlineData(402, "Payment required", "hết tiền hoặc hết hạn mức")]
    [InlineData(404, "model not found", "base_url và model_id")]
    public async Task ConfigurationErrorsStopAtOnce(int status, string message, string expectedHint)
    {
        var http = new FakeHttp().Respond((HttpStatusCode)status, ErrorBody(message));

        var error = await Assert.ThrowsAsync<ArrowException>(() => Call(Client(http)));

        Assert.Equal(ErrorCodes.B2LlmConfig, error.Code);
        Assert.Single(http.Requests);
        Assert.Empty(_retries);
        Assert.Contains($"HTTP {status}", error.Message, StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedHint, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(429, "You exceeded your current quota: insufficient_quota")]
    [InlineData(500, "billing hard limit reached")]
    public async Task QuotaAndBillingMessagesAreConfigurationErrorsWhateverTheStatus(int status, string message)
    {
        var http = new FakeHttp().Respond((HttpStatusCode)status, ErrorBody(message));
        var error = await Assert.ThrowsAsync<ArrowException>(() => Call(Client(http)));
        Assert.Equal(ErrorCodes.B2LlmConfig, error.Code);
        Assert.Contains("hết tiền hoặc hết hạn mức", error.Message, StringComparison.Ordinal);
        Assert.Single(http.Requests);
    }

    // P03 §13 and P09 §4.5: a key that the provider echoes back never reaches the message, the log or an event.
    [Fact]
    public async Task AKeyEchoedByTheProviderIsRedacted()
    {
        Redactor.Register(FakeKey);
        var http = new FakeHttp().Respond(HttpStatusCode.Unauthorized, ErrorBody("Incorrect API key provided: " + FakeKey));

        var error = await Assert.ThrowsAsync<ArrowException>(() => Call(Client(http)));

        Assert.DoesNotContain(FakeKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("***", error.Message, StringComparison.Ordinal);

        var serverError = new FakeHttp();
        for (var i = 0; i < 4; i++)
        {
            serverError.Respond(HttpStatusCode.BadGateway, $"upstream rejected {FakeKey}");
        }

        var unavailable = await Assert.ThrowsAsync<ArrowException>(() => Call(Client(serverError)));
        Assert.DoesNotContain(FakeKey, unavailable.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Lines, l => l.Contains(FakeKey, StringComparison.Ordinal));
        Assert.DoesNotContain(_retries, r => r.Reason.Contains(FakeKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingStopsTheCallWithoutRetry()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var http = new FakeHttp().Hang();
        var task = Client(http).CompleteAsync(Request(), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(http.Requests);
        Assert.Empty(_retries);
    }
}
