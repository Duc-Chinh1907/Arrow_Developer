using System.Diagnostics;
using System.Globalization;
using System.Text;
using Arrow.DataAccess.IO;
using Arrow.DataAccess.Security;

namespace Arrow.DataAccess.Processes;

/// <summary>An external command to run.</summary>
/// <param name="FileName">Program to start, e.g. <c>mvn.cmd</c> or an absolute path to <c>java</c>.</param>
/// <param name="Arguments">Arguments, passed one by one without shell quoting.</param>
/// <param name="WorkingDirectory">Folder to run in.</param>
/// <param name="Environment">Variables to set; a null value removes the variable.</param>
/// <param name="Timeout">The process tree is killed after this time; null means no limit.</param>
/// <param name="StandardInput">Text written to stdin, which is then closed. Stdin is always closed, so no command waits for a keyboard.</param>
/// <param name="LogFile">Log file of P00 §5; null means no log.</param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    string? StandardInput = null,
    string? LogFile = null);

/// <summary>Result of a command.</summary>
/// <param name="ExitCode">Exit code; null when the process was killed for its timeout.</param>
/// <param name="TimedOut">True when the timeout killed it.</param>
/// <param name="Output">stdout and stderr in arrival order, LF line endings; above <see cref="ProcessRunner.LimitBytes"/> only the first and last 2 MB are kept.</param>
/// <param name="Duration">Wall time.</param>
/// <param name="OutputTruncated">True when the middle of the output was cut.</param>
public sealed record ProcessResult(int? ExitCode, bool TimedOut, string Output, TimeSpan Duration, bool OutputTruncated = false)
{
    /// <summary>Exit code 0 and no timeout.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>The last non-empty lines of the output, for error messages.</summary>
    public string Tail(int lines = 20) =>
        string.Join('\n', Output.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(lines));
}

/// <summary>Runs external commands (P00 §5).</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs a command and waits for it. On timeout the whole process tree is killed and the result has
    /// <see cref="ProcessResult.TimedOut"/>; on cancellation the tree is killed and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary><see cref="IProcessRunner"/> on <see cref="Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Output above this size is cut in the middle (P00 §5).</summary>
    public const int LimitBytes = 5 * 1024 * 1024;

    /// <summary>Output kept from the start of a cut run.</summary>
    public const int HeadBytes = 2 * 1024 * 1024;

    /// <summary>Output kept from the end of a cut run.</summary>
    public const int TailBytes = 2 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in spec.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        var output = new CappedOutput(HeadBytes, LimitBytes - HeadBytes, TailBytes);
        var watch = Stopwatch.StartNew();
        var timedOut = false;
        int? exitCode = null;
        var cancelled = false;
        var started = false;
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => output.AppendLine(e.Data);
        try
        {
            process.Start();
            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (spec.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(spec.Timeout ?? System.Threading.Timeout.InfiniteTimeSpan);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                // Also waits for the end of stdout and stderr, so no output is lost.
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                timedOut = !ct.IsCancellationRequested;
                cancelled = ct.IsCancellationRequested;
            }
        }
        finally
        {
            watch.Stop();
            if (spec.LogFile is not null)
            {
                var exit = exitCode?.ToString(CultureInfo.InvariantCulture)
                    ?? (!started ? "not-started" : cancelled ? "cancelled" : "killed");
                WriteLog(spec, output, exit, timedOut, watch.Elapsed);
            }
        }

        ct.ThrowIfCancellationRequested();
        var (text, truncated) = output.Snapshot();
        return new ProcessResult(exitCode, timedOut, text, watch.Elapsed, truncated);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied while the tree was going away; nothing more to kill.
        }
    }

    /// <summary>
    /// P00 §5: first line has the command, the folder and the JDK; last line has the exit code, the time and the timeout.
    /// Everything is redacted, because a command line or its output may carry a key.
    /// </summary>
    private static void WriteLog(ProcessSpec spec, CappedOutput output, string exit, bool timedOut, TimeSpan duration)
    {
        var jdk = spec.Environment?.GetValueOrDefault("JAVA_HOME") ?? "-";
        var command = string.Join(' ', new[] { spec.FileName }.Concat(spec.Arguments).Select(Quote));
        var (text, _) = output.Snapshot();
        var log = new StringBuilder()
            .Append("$ ").Append(command).Append(" | dir=").Append(spec.WorkingDirectory).Append(" | jdk=").Append(jdk).Append('\n')
            .Append(text)
            .Append(CultureInfo.InvariantCulture, $"# exit={exit} seconds={duration.TotalSeconds:0.000} timeout={(timedOut ? "yes" : "no")}\n")
            .ToString();
        AtomicFile.WriteAllBytes(spec.LogFile!, Utf8NoBom.GetBytes(Redactor.Redact(log)));
    }

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;

    /// <summary>
    /// Thread-safe output buffer (sizes in UTF-8 bytes). It holds the first <c>head</c> bytes and a rolling window of the
    /// last <c>window</c> bytes, so a build that prints hundreds of megabytes cannot exhaust memory. When anything was
    /// dropped, the snapshot keeps only the last <c>finalTail</c> bytes of the window.
    /// </summary>
    private sealed class CappedOutput(int head, int window, int finalTail)
    {
        private readonly StringBuilder _head = new();
        private readonly LinkedList<string> _tail = new();
        private int _headBytes;
        private int _tailBytes;
        private long _droppedBytes;

        public void AppendLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            lock (_head)
            {
                if (_tail.Count == 0 && _headBytes + bytes <= head)
                {
                    _head.Append(line).Append('\n');
                    _headBytes += bytes;
                    return;
                }

                _tail.AddLast(line);
                _tailBytes += bytes;
                while (_tailBytes > window && _tail.First is { } first)
                {
                    var size = Encoding.UTF8.GetByteCount(first.Value) + 1;
                    _tail.RemoveFirst();
                    _tailBytes -= size;
                    _droppedBytes += size;
                }
            }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (_head)
            {
                var lines = _tail.ToList();
                var dropped = _droppedBytes;
                if (dropped > 0)
                {
                    var bytes = _tailBytes;
                    var skip = 0;
                    while (bytes > finalTail && skip < lines.Count)
                    {
                        var size = Encoding.UTF8.GetByteCount(lines[skip]) + 1;
                        bytes -= size;
                        dropped += size;
                        skip++;
                    }

                    lines = lines[skip..];
                }

                var text = new StringBuilder(_head.ToString());
                if (dropped > 0)
                {
                    text.Append(CultureInfo.InvariantCulture, $"[... {dropped} bytes omitted ...]\n");
                }

                foreach (var line in lines)
                {
                    text.Append(line).Append('\n');
                }

                return (text.ToString(), dropped > 0);
            }
        }
    }
}
