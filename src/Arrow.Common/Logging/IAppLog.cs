namespace Arrow.Common.Logging;

/// <summary>
/// The tool log <c>ARROW_HOME/logs/arrow-&lt;yyyyMMdd&gt;.log</c> (P09 §7): commands, warnings, errors and exceptions with
/// stack traces. Every line is redacted before it is written. Debug lines are kept only with <c>--verbose</c>.
/// </summary>
public interface IAppLog
{
    /// <summary>Path of today's log file, shown to the user after an internal error.</summary>
    string FilePath { get; }

    void Debug(string message);

    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);
}
