namespace Arrow.DataAccess.Llm;

/// <summary>The <see cref="HttpClient"/> shared by the calls of a run (P03 §3.4).</summary>
public static class LlmHttpClientFactory
{
    /// <summary>
    /// A client with a pooled connection lifetime of 2 minutes and the system proxy (<c>HTTPS_PROXY</c>). Its own timeout is off: every
    /// send has its own timeout, <c>llm.request_timeout_seconds</c>.
    /// </summary>
    public static HttpClient Create() => new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), UseProxy = true })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
