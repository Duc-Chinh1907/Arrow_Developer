using System.Net;
using System.Text;
using Arrow.Business.Llm;
using Arrow.Common;
using Arrow.Common.Config;
using Arrow.Common.Errors;
using Arrow.Common.Events;
using Arrow.Common.Llm;
using Arrow.Common.Secrets;
using Arrow.DataAccess.Security;
using Arrow.DataAccess.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Arrow.Business.Tests.Llm;

/// <summary>The chain of P03 §12 built by <see cref="LlmClientFactory"/>: replay → gateway → provider, with the key handling of P09 §4.</summary>
public sealed class LlmClientFactoryTests : IDisposable
{
    private const string FakeKey = "sk-test-SECRET-123";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-llm-factory", Guid.NewGuid().ToString("N")[..10]);
    private readonly RunStore _runs;
    private readonly RunHandle _run;
    private readonly FakeTimeProvider _time = new();

    public LlmClientFactoryTests()
    {
        var home = new ArrowHome(_root);
        home.EnsureCreated();
        _runs = new RunStore(home);
        var info = new RunInfo("20261113-110000-wxyz", "test", DateTimeOffset.UtcNow, null, RunStatus.Running, ["gen"],
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

    private sealed class Secrets(string? key) : ISecretStore
    {
        public SecretValue? Get(string reference) => key is null ? null : new SecretValue(key);

        public void Set(string reference, SecretValue value) => throw new NotSupportedException();

        public bool Remove(string reference) => throw new NotSupportedException();

        public IReadOnlyList<SecretInfo> List() => [];
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok(string model = "gpt-4.1-mini-2025-04-14") => Json(HttpStatusCode.OK, new
    {
        model,
        choices = new[] { new { message = new { content = "class A {}" }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 10, completion_tokens = 5 },
    });

    private LlmClientFactory Factory(ISecretStore secrets, Handler? handler = null, string? fakeRules = null) =>
        new(secrets, _runs, "arrow-cli/1.2.3", http: handler is null ? null : new HttpClient(handler), time: _time, fakeRulesFile: fakeRules);

    private static LlmLimits Limits(long? budget = null) => new(2, 60, budget, TimeSpan.FromSeconds(300));

    [Fact]
    public async Task BuildsTheChainAndReplaysOnResume()
    {
        var handler = new Handler(_ => Ok());
        var factory = Factory(new Secrets(FakeKey), handler);
        var model = LlmFixtures.Model(prefix: "gpt-4.1-mini");
        var request = LlmFixtures.Request();
        var ct = TestContext.Current.CancellationToken;

        var first = await factory.Create(model, _run, Limits()).CompleteAsync(request, ct);
        Assert.False(first.Replayed);
        Assert.Equal("Bearer " + FakeKey, handler.Requests.Single().Headers.Authorization!.ToString());
        Assert.Equal("arrow-cli/1.2.3", handler.Requests.Single().Headers.UserAgent.ToString());

        // A resumed run builds the chain again over the same run folder: the finished call is played back.
        var progress = new LlmFixtures.ListProgress();
        var second = await factory.Create(model, _run, Limits(), progress).CompleteAsync(request, ct);
        Assert.True(second.Replayed);
        Assert.Single(handler.Requests);
        Assert.Contains(progress.Events, e => e.Type == ArrowEventTypes.LlmReplayed);
        Assert.Equal(first.PromptTokens, second.PromptTokens);
    }

    [Fact]
    public async Task AnthropicProviderUsesTheMessagesApi()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"model":"claude-x","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json"),
        });
        var model = LlmFixtures.Model() with { Kind = ProviderKind.Anthropic, BaseUrl = new Uri("https://api.anthropic.com"), ModelId = "claude-x" };
        await Factory(new Secrets(FakeKey), handler).Create(model, _run, Limits()).CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Requests.Single().RequestUri!.AbsoluteUri);
        Assert.Equal(FakeKey, handler.Requests.Single().Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public void AMissingKeyIsReportedWithTheNamesToUse()
    {
        var model = LlmFixtures.Model() with { SecretRef = "my-team", ProviderName = "teamo" };
        var error = Assert.Throws<ArrowException>(() => Factory(new Secrets(null)).Create(model, _run, Limits()));
        Assert.Equal(ErrorCodes.CfgSecretMissing, error.Code);
        Assert.Equal(("teamo", "my-team", "ARROW_SECRET_MY_TEAM"), (error.Args["provider"], error.Args["ref"], error.Args["env_var"]));
    }

    [Fact]
    public async Task AProviderWithoutASecretRefNeedsNoKey()
    {
        var handler = new Handler(_ => Ok());
        var model = LlmFixtures.Model() with { SecretRef = null, BaseUrl = new Uri("http://localhost:11434/v1") };
        await Factory(new Secrets(null), handler).Create(model, _run, Limits()).CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Null(handler.Requests.Single().Headers.Authorization);
    }

    // P09 §11 and P03 §13: after a run the fake key is in no file under ARROW_HOME, even when the provider echoes it in an error.
    [Fact]
    public async Task TheKeyIsInNoFileOfTheRun()
    {
        var failing = new Handler(_ => Json(HttpStatusCode.Unauthorized, new { error = new { message = "Incorrect API key provided: " + FakeKey } }));
        var client = Factory(new Secrets(FakeKey), failing).Create(LlmFixtures.Model(), _run, Limits());
        var error = await Assert.ThrowsAsync<ArrowException>(() => client.CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(FakeKey, error.Message, StringComparison.Ordinal);

        var ok = new Handler(_ => Ok());
        await Factory(new Secrets(FakeKey), ok).Create(LlmFixtures.Model(), _run, Limits()).CompleteAsync(LlmFixtures.Request("gen/other"), TestContext.Current.CancellationToken);

        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains(FakeKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARulesFileReplacesTheProviderAndNeedsNoKey()
    {
        var rules = Path.Combine(_root, "rules.json");
        File.WriteAllText(rules, """{ "rules": [ { "when": { "call_id_prefix": "gen/" }, "respond_text": "class FromFake {}" } ] }""");
        var factory = Factory(new Secrets(null), fakeRules: rules);
        Assert.True(factory.IsFake);

        var response = await factory.Create(LlmFixtures.Model(), _run, Limits()).CompleteAsync(LlmFixtures.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("class FromFake {}", response.Content);
        Assert.Equal("gpt-4.1-mini", response.ModelId);
    }

    [Fact]
    public async Task TheBudgetOfTheRunIsSharedByEveryCallOfTheChain()
    {
        var handler = new Handler(_ => Ok());
        var client = Factory(new Secrets(FakeKey), handler).Create(LlmFixtures.Model(), _run, Limits(budget: 20));
        var ct = TestContext.Current.CancellationToken;

        await client.CompleteAsync(LlmFixtures.Request("gen/a"), ct);
        await client.CompleteAsync(LlmFixtures.Request("gen/b"), ct);
        var error = await Assert.ThrowsAsync<ArrowException>(() => client.CompleteAsync(LlmFixtures.Request("gen/c"), ct));
        Assert.Equal(ErrorCodes.B2TokenBudget, error.Code);
        Assert.Equal(2, handler.Requests.Count);
    }
}
