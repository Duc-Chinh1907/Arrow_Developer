using System.Collections;

namespace Arrow.Cli;

/// <summary>
/// The terminal the CLI talks to: streams, whether they are redirected, environment variables and the current folder.
/// <see cref="CliApp"/> uses only this, so tests run the whole CLI in-process with their own streams.
/// </summary>
public sealed record CliConsole(
    TextReader In,
    TextWriter Out,
    TextWriter Error,
    bool IsInputRedirected,
    bool IsOutputRedirected,
    IReadOnlyDictionary<string, string?> Environment,
    string CurrentDirectory)
{
    /// <summary>
    /// P08 §4.3: no keyboard when stdin is redirected or <c>CI=true</c>. Commands then never wait for an answer.
    /// </summary>
    public bool HasKeyboard => !IsInputRedirected && !string.Equals(Environment.GetValueOrDefault("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The process console.</summary>
    public static CliConsole System() => new(
        Console.In,
        Console.Out,
        Console.Error,
        Console.IsInputRedirected,
        Console.IsOutputRedirected,
        global::System.Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
        global::System.Environment.CurrentDirectory);
}
