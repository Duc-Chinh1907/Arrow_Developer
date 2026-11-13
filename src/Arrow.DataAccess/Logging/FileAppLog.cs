using System.Globalization;
using System.Text;
using Arrow.Common.Logging;
using Arrow.DataAccess.Security;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.Logging;

/// <summary>
/// <see cref="IAppLog"/> on <c>ARROW_HOME/logs/arrow-&lt;yyyyMMdd&gt;.log</c> (P09 §7). Files older than 14 days are deleted
/// when the log opens. With <c>--verbose</c>, debug lines are also kept and every line is echoed to stderr.
/// </summary>
public sealed class FileAppLog : IAppLog
{
    /// <summary>Days a log file is kept (P09 §7).</summary>
    public const int RetentionDays = 14;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _lock = new();
    private readonly bool _verbose;
    private readonly TextWriter? _echo;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _logsDir;

    public FileAppLog(ArrowHome home, bool verbose = false, TextWriter? echo = null, Func<DateTimeOffset>? clock = null)
    {
        _logsDir = home.LogsDir;
        _verbose = verbose;
        _echo = verbose ? echo : null;
        _clock = clock ?? (() => DateTimeOffset.Now);
        DeleteOldFiles();
    }

    public string FilePath => Path.Combine(_logsDir, $"arrow-{_clock().ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    public void Debug(string message)
    {
        if (_verbose)
        {
            Write("DBG", message, null);
        }
    }

    public void Info(string message) => Write("INF", message, null);

    public void Warning(string message) => Write("WRN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var text = new StringBuilder()
            .Append(_clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ").Append(message);
        if (exception is not null)
        {
            text.Append('\n').Append(exception);
        }

        var line = Redactor.Redact(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)) + "\n";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_logsDir);
                File.AppendAllText(FilePath, line, Utf8NoBom);
            }
            catch (IOException)
            {
                // The log must never break a command; a locked or full disk only loses log lines.
            }
            catch (UnauthorizedAccessException)
            {
            }

            _echo?.Write(line);
        }
    }

    private void DeleteOldFiles()
    {
        if (!Directory.Exists(_logsDir))
        {
            return;
        }

        var limit = _clock().AddDays(-RetentionDays);
        foreach (var file in Directory.GetFiles(_logsDir, "arrow-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file)["arrow-".Length..];
            if (DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < limit.Date)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
