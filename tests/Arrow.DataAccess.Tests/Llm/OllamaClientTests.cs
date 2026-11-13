using System.Net;
using Arrow.DataAccess.Llm;

namespace Arrow.DataAccess.Tests.Llm;

/// <summary>The Ollama checks of P03 §8: running, model installed, <c>num_ctx</c>.</summary>
public class OllamaClientTests
{
    private static readonly Uri Base = new("http://localhost:11434/v1");

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://localhost:8080/v1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    public void RecognizesTheOllamaAddress(string url, bool expected) => Assert.Equal(expected, OllamaClient.IsOllama(new Uri(url)));

    [Theory]
    [InlineData("num_ctx                        40960\nstop                           \"<|im_end|>\"", 40960)]
    [InlineData("temperature 0.7\nnum_ctx 4096", 4096)]
    [InlineData("temperature 0.7", null)]
    [InlineData(null, null)]
    public void ReadsNumCtxFromTheParametersText(string? parameters, int? expected) => Assert.Equal(expected, OllamaClient.NumCtx(parameters));

    [Fact]
    public async Task ReportsTheContextWindowOfAnInstalledModel()
    {
        var http = new FakeHttp()
            .Respond(HttpStatusCode.OK, """{"models":[{"name":"qwen3-coder:30b"},{"name":"llama3:latest"}]}""")
            .Respond(HttpStatusCode.OK, """{"parameters":"num_ctx 8192\ntemperature 0.2"}""");

        var status = await new OllamaClient(new HttpClient(http)).InspectAsync(Base, "qwen3-coder:30b", TestContext.Current.CancellationToken);

        Assert.Equal(new OllamaStatus(true, true, 8192, null), status);
        Assert.Equal("http://localhost:11434/api/tags", http.Requests[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("http://localhost:11434/api/show", http.Requests[1].Request.RequestUri!.AbsoluteUri);
        Assert.Contains("\"model\":\"qwen3-coder:30b\"", http.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelWithoutNumCtxHasNone()
    {
        var http = new FakeHttp()
            .Respond(HttpStatusCode.OK, """{"models":[{"name":"llama3:latest"}]}""")
            .Respond(HttpStatusCode.OK, """{"parameters":"stop \"x\""}""");
        var status = await new OllamaClient(new HttpClient(http)).InspectAsync(Base, "llama3", TestContext.Current.CancellationToken);
        Assert.Equal(new OllamaStatus(true, true, null, null), status);
    }

    [Fact]
    public async Task AMissingModelIsReportedWithoutAskingForItsParameters()
    {
        var http = new FakeHttp().Respond(HttpStatusCode.OK, """{"models":[{"name":"other:latest"}]}""");
        var status = await new OllamaClient(new HttpClient(http)).InspectAsync(Base, "qwen3-coder:30b", TestContext.Current.CancellationToken);
        Assert.Equal(new OllamaStatus(true, false, null, null), status);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task OllamaThatIsNotRunningIsAResultNotAnException()
    {
        var down = await new OllamaClient(new HttpClient(new FakeHttp().Fail(new HttpRequestException("Connection refused"))))
            .InspectAsync(Base, "m", TestContext.Current.CancellationToken);
        Assert.False(down.Running);
        Assert.Contains("Connection refused", down.Error, StringComparison.Ordinal);

        var broken = await new OllamaClient(new HttpClient(new FakeHttp().Respond(HttpStatusCode.InternalServerError, "oops")))
            .InspectAsync(Base, "m", TestContext.Current.CancellationToken);
        Assert.False(broken.Running);
        Assert.Contains("500", broken.Error, StringComparison.Ordinal);
    }
}
