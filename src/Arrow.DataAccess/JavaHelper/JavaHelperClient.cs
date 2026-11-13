using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Arrow.Common.Errors;
using Arrow.Common.Json;
using Arrow.Common.Logging;

namespace Arrow.DataAccess.JavaHelper;

/// <summary>How to start java-helper and how long its commands may take (P10 §3.1, §5).</summary>
/// <param name="JavaExecutable">The <c>java</c> of a JDK ≥ 17 (<see cref="JavaHelperJar.FindJava"/>).</param>
/// <param name="JarPath">The extracted jar (<see cref="JavaHelperJar.Ensure"/>).</param>
public sealed record JavaHelperOptions(string JavaExecutable, string JarPath)
{
    /// <summary>The version <c>ping</c> must answer; another one is <c>JH_VERSION_MISMATCH</c>.</summary>
    public string ExpectedVersion { get; init; } = JavaHelperJar.Version;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ListClassesTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan ClassApiTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Largest code (UTF-8 bytes) sent in one command (P10 §5).</summary>
    public int MaxCodeBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>
/// One java-helper process, started on the first command and reused (P10 §5). Commands are sent one at a time;
/// a process that dies or times out is restarted once and the command resent; the second failure is
/// <c>JH_START_FAILED</c> or <c>JH_TIMEOUT</c>. An <c>ok: false</c> answer is a <see cref="JavaHelperCommandException"/>.
/// </summary>
public sealed class JavaHelperClient : IJavaHelper
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly JavaHelperOptions _options;
    private readonly IAppLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private long _nextId;
    private bool _disposed;

    public JavaHelperClient(JavaHelperOptions options, IAppLog? log = null)
    {
        _options = options;
        _log = log;
    }

    /// <summary>Id of the running process; null before the first command or after it stopped.</summary>
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    public async Task<string> PingAsync(CancellationToken ct) =>
        (await SendAsync("ping", new Dictionary<string, object?>(), 0, ct).ConfigureAwait(false)).GetProperty("version").GetString()!;

    public async Task<JavaParseResult> ParseAsync(string code, CancellationToken ct) =>
        Read<JavaParseResult>(await SendAsync("parse", Args(("code", code)), Size(code), ct).ConfigureAwait(false));

    public async Task<RenameResult> RenameClassAsync(string code, string newName, string? package, CancellationToken ct)
    {
        var args = Args(("code", code), ("new_name", newName));
        if (package is not null)
        {
            args["package"] = package;
        }

        return Read<RenameResult>(await SendAsync("rename_class", args, Size(code), ct).ConfigureAwait(false));
    }

    public async Task<HashResult> StripCommentsHashAsync(string code, CancellationToken ct) =>
        Read<HashResult>(await SendAsync("strip_comments_hash", Args(("code", code)), Size(code), ct).ConfigureAwait(false));

    public async Task<ShrinkResult> ShrinkSourceAsync(string code, int maxChars, CancellationToken ct) =>
        Read<ShrinkResult>(await SendAsync("shrink_source", Args(("code", code), ("max_chars", maxChars)), Size(code), ct).ConfigureAwait(false));

    public async Task<SpliceResult> SpliceMethodsAsync(string baseCode, string response, IReadOnlyList<string> methods, CancellationToken ct) =>
        Read<SpliceResult>(await SendAsync("splice_methods", Args(("base", baseCode), ("response", response), ("methods", methods)),
            Size(baseCode) + Size(response), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<string>> ListTestMethodsAsync(string code, CancellationToken ct) =>
        Read<NamesResult>(await SendAsync("list_test_methods", Args(("code", code)), Size(code), ct).ConfigureAwait(false)).Methods ?? [];

    public async Task<AssertionCount> CountAssertionsAsync(string code, CancellationToken ct) =>
        Read<AssertionCount>(await SendAsync("count_assertions", Args(("code", code)), Size(code), ct).ConfigureAwait(false));

    public async Task<IReadOnlyDictionary<int, string?>> MethodAtLineAsync(string code, IReadOnlyList<int> lines, CancellationToken ct)
    {
        var result = Read<LinesResult>(await SendAsync("method_at_line", Args(("code", code), ("lines", lines)), Size(code), ct).ConfigureAwait(false));
        return (result.Methods ?? []).ToDictionary(e => int.Parse(e.Key, CultureInfo.InvariantCulture), e => e.Value);
    }

    public async Task<ClassApiResult> ClassApiAsync(IReadOnlyList<string> sources, IReadOnlyList<string> classes, IReadOnlyList<string> classpath, CancellationToken ct) =>
        Read<ClassApiResult>(await SendAsync("class_api", Args(("sources", sources), ("classes", classes), ("classpath", classpath)),
            sources.Sum(Size), ct, _options.ClassApiTimeout).ConfigureAwait(false));

    public async Task<IReadOnlyList<MethodSourceEntry>> MethodSourceAsync(string code, IReadOnlyList<string> names, CancellationToken ct) =>
        Read<MethodSourcesResult>(await SendAsync("method_source", Args(("code", code), ("names", names)), Size(code), ct).ConfigureAwait(false)).Methods ?? [];

    public async Task<CalledMethodsResult> CalledMethodsAsync(string testCode, IReadOnlyList<string> testMethods, string cutCode, CancellationToken ct) =>
        Read<CalledMethodsResult>(await SendAsync("called_methods", Args(("test_code", testCode), ("test_methods", testMethods), ("cut_code", cutCode)),
            Size(testCode) + Size(cutCode), ct).ConfigureAwait(false));

    public async Task<ListClassesResult> ListClassesAsync(IReadOnlyList<string> roots, int maxFiles, CancellationToken ct) =>
        Read<ListClassesResult>(await SendAsync("list_classes", Args(("roots", roots), ("max_files", maxFiles)), 0, ct, _options.ListClassesTimeout).ConfigureAwait(false));

    /// <summary>Sends <c>shutdown</c>, waits 2 s, then kills the process if it is still alive (P10 §5).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            try
            {
                if (_process is { HasExited: false } process)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync(Request("shutdown", new Dictionary<string, object?>())).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync().ConfigureAwait(false);
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
                    {
                        // The process did not stop by itself; it is killed below.
                    }
                }
            }
            finally
            {
                Stop();
                _gate.Release();
            }
        }
        else
        {
            Stop();
        }

        _gate.Dispose();
    }

    private async Task<JsonElement> SendAsync(string command, Dictionary<string, object?> args, int codeBytes, CancellationToken ct, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (codeBytes > _options.MaxCodeBytes)
        {
            throw new ArrowException(ErrorCodes.JhInputTooLarge, $"{command}: {codeBytes} byte > {_options.MaxCodeBytes} byte");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await EnsureStartedAsync(ct).ConfigureAwait(false);
                    var result = await ExchangeAsync(command, args, timeout ?? _options.CommandTimeout, ct).ConfigureAwait(false);
                    _log?.Debug(string.Create(CultureInfo.InvariantCulture, $"java-helper {command} code={codeBytes}B {watch.Elapsed.TotalMilliseconds:0}ms ok"));
                    return result;
                }
                catch (HelperFailure failure)
                {
                    _log?.Warning($"java-helper {command}: {failure.Message} (lần {attempt})");
                    Stop();
                    if (attempt >= 2)
                    {
                        throw failure.TimedOut
                            ? new ArrowException(ErrorCodes.JhTimeout, $"{command}: {failure.Message}")
                            : new ArrowException(ErrorCodes.JhStartFailed, $"{command}: {failure.Message}");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The answer of the cancelled command may still come; a fresh process avoids reading it later.
                    Stop();
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts the process if needed and checks its version with <c>ping</c>.</summary>
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        Stop();
        var start = new ProcessStartInfo(_options.JavaExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // No BOM: it would break the first JSON line read by java-helper.
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-Xss8m", "-Xmx512m", "-Dfile.encoding=UTF-8", "-jar", _options.JarPath })
        {
            start.ArgumentList.Add(argument);
        }

        // Options of the user's environment must not change java-helper or add lines to its streams.
        foreach (var variable in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS" })
        {
            start.Environment.Remove(variable);
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _log?.Info("java-helper stderr: " + e.Data);
            }
        };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new ArrowException(ErrorCodes.JhStartFailed, $"{_options.JavaExecutable}: {ex.Message}", inner: ex);
        }

        process.BeginErrorReadLine();
        _process = process;
        _log?.Info($"java-helper started, pid {process.Id}");

        var version = (await ExchangeAsync("ping", new Dictionary<string, object?>(), _options.CommandTimeout, ct).ConfigureAwait(false))
            .GetProperty("version").GetString();
        if (version != _options.ExpectedVersion)
        {
            Stop();
            throw new ArrowException(ErrorCodes.JhVersionMismatch, args: new Dictionary<string, string>
            {
                ["expected"] = _options.ExpectedVersion,
                ["actual"] = version ?? "?",
            });
        }
    }

    /// <summary>Writes one request line and reads the answer with the same id.</summary>
    private async Task<JsonElement> ExchangeAsync(string command, Dictionary<string, object?> args, TimeSpan timeout, CancellationToken ct)
    {
        var process = _process ?? throw new HelperFailure("not started", timedOut: false);
        var id = Interlocked.Increment(ref _nextId);
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(Request(command, args, id).AsMemory(), linked.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false)
                    ?? throw new HelperFailure("the process exited", timedOut: false);
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                using var answer = JsonDocument.Parse(line);
                var root = answer.RootElement;
                if (!root.TryGetProperty("id", out var answerId) || answerId.ValueKind != JsonValueKind.Number || answerId.GetInt64() != id)
                {
                    // An answer to an earlier request; never expected because commands are sequential.
                    continue;
                }

                if (root.GetProperty("ok").GetBoolean())
                {
                    return root.GetProperty("result").Clone();
                }

                throw new JavaHelperCommandException(command, root.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "");
            }
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new HelperFailure($"no answer after {timeout.TotalSeconds:0} s", timedOut: true);
        }
        catch (IOException ex)
        {
            throw new HelperFailure(ex.Message, timedOut: false);
        }
        catch (JsonException ex)
        {
            throw new HelperFailure("invalid answer: " + ex.Message, timedOut: false);
        }
    }

    private void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string Request(string command, Dictionary<string, object?> args, long id = 0) =>
        ArrowJson.Serialize(new Dictionary<string, object?> { ["id"] = id, ["command"] = command, ["args"] = args }, indented: false);

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    private static int Size(string text) => Encoding.UTF8.GetByteCount(text);

    private static T Read<T>(JsonElement result) => ArrowJson.Deserialize<T>(result.GetRawText());

    private sealed record NamesResult(bool Parsed, IReadOnlyList<string>? Methods);

    private sealed record LinesResult(bool Parsed, Dictionary<string, string?>? Methods);

    private sealed record MethodSourcesResult(bool Parsed, IReadOnlyList<MethodSourceEntry>? Methods);

    /// <summary>The process died or did not answer in time: worth one restart (P10 §5).</summary>
    private sealed class HelperFailure(string message, bool timedOut) : Exception(message)
    {
        public bool TimedOut { get; } = timedOut;
    }
}
