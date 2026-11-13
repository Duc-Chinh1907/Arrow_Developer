using System.Net;
using System.Text;
using System.Text.Json;
using Arrow.Common.Llm;
using Arrow.Common.Logging;
using Arrow.Common.Secrets;
using Arrow.DataAccess.Llm;
using Microsoft.Extensions.Time.Testing;

namespace Arrow.DataAccess.Tests.Llm;

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a queue and records the requests (P03 §13: "HTTP giả").</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _answers = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public FakeHttp Respond(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        _answers.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
        return this;
    }

    public FakeHttp Fail(Exception exception)
    {
        _answers.Enqueue((_, _) => throw exception);
        return this;
    }

    /// <summary>The send never answers; it ends only when its token is cancelled (a timeout).</summary>
    public FakeHttp Hang()
    {
        _answers.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        if (_answers.Count == 0)
        {
            throw new InvalidOperationException("FakeHttp: no answer left for " + request.RequestUri);
        }

        return await _answers.Dequeue()(request, cancellationToken);
    }
}

/// <summary>Collects the lines of the tool log.</summary>
internal sealed class ListLog : IAppLog
{
    public List<string> Lines { get; } = [];

    public string FilePath => "memory";

    public void Debug(string message) => Lines.Add(message);

    public void Info(string message) => Lines.Add(message);

    public void Warning(string message) => Lines.Add(message);

    public void Error(string message, Exception? exception = null) => Lines.Add(message + exception);
}

internal static class LlmTestSupport
{
    /// <summary>The fake key of CLAUDE.md; never a real one.</summary>
    public const string FakeKey = "sk-test-SECRET-123";

    public static LlmRequest Request(string callId = "gen/com.acme.Order", string model = "gpt-x", params (string Name, object Value)[] parameters)
    {
        var map = new Dictionary<string, JsonElement>
        {
            ["temperature"] = JsonSerializer.SerializeToElement(0),
            ["max_tokens"] = JsonSerializer.SerializeToElement(16384),
        };
        foreach (var (name, value) in parameters)
        {
            map[name] = JsonSerializer.SerializeToElement(value);
        }

        return new LlmRequest(callId, model, [new LlmMessage("system", "You write tests."), new LlmMessage("user", "Test class Order.")], map);
    }

    public static LlmEndpoint Endpoint(string baseUrl = "https://api.teamorouter.com/v1", string modelId = "gpt-4.1-mini", string? key = FakeKey, string? secretRef = "teamo") =>
        new(new Uri(baseUrl), modelId, key is null ? null : new SecretValue(key), secretRef);

    /// <summary>
    /// Runs <paramref name="call"/> while moving the fake clock forward, so that the waits and timeouts of the retry loop end at once.
    /// The step is large enough to pass a 300 s timeout in one move.
    /// </summary>
    public static async Task<T> Drive<T>(Task<T> call, FakeTimeProvider time)
    {
        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(301));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        return await call;
    }

    public static string OpenAiBody(string content = "OK", string model = "gpt-4.1-mini-2025-04-14", string finish = "stop", int prompt = 100, int completion = 20) =>
        JsonSerializer.Serialize(new
        {
            id = "x",
            model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finish } },
            usage = new { prompt_tokens = prompt, completion_tokens = completion },
        });
}
