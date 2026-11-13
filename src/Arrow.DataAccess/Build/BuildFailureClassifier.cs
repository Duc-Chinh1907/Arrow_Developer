using System.IO.Compression;
using System.Text.RegularExpressions;
using Arrow.DataAccess.Processes;

namespace Arrow.DataAccess.Build;

/// <summary>Why a build command failed.</summary>
public enum BuildFailureKind
{
    /// <summary>It did not fail.</summary>
    None,

    /// <summary>Killed after <c>step_timeout_seconds</c>: retried as an infrastructure error.</summary>
    Timeout,

    /// <summary>Download or connection error: retried as an infrastructure error.</summary>
    Network,

    /// <summary>The build itself failed (compile error, missing artifact…): another JDK may help, retrying does not.</summary>
    Build,
}

/// <summary>Classifies failed build commands and reads tool versions (P01 §4.2, §4.4, §6.2).</summary>
public static partial class BuildFailureClassifier
{
    // from ARROW_Framework/src/Arrow.Service/External/BuildRunner.cs
    private static readonly string[] NetworkMarkers =
    [
        "connection reset", "connection refused", "connect timed out", "read timed out", "connection timed out", "unknownhostexception",
        "unknown host", "network is unreachable", "no route to host", "temporary failure in name resolution", "remote host terminated the handshake",
        "premature end of content-length", "socketexception", "sockettimeoutexception", "could not resolve host",
    ];

    // from ARROW_Framework/src/Arrow.Service/External/BuildRunner.cs
    [GeneratedRegex(@"status code:?\s*(5\d\d|429)\b|received status code (5\d\d|429)", RegexOptions.IgnoreCase)]
    private static partial Regex ServerError();

    /// <summary>Timeout, network error, build error, or none.</summary>
    public static BuildFailureKind Classify(ProcessResult result)
    {
        if (result.TimedOut)
        {
            return BuildFailureKind.Timeout;
        }

        if (result.ExitCode == 0)
        {
            return BuildFailureKind.None;
        }

        var text = result.Output.ToLowerInvariant();
        return NetworkMarkers.Any(text.Contains) || ServerError().IsMatch(result.Output) ? BuildFailureKind.Network : BuildFailureKind.Build;
    }

    /// <summary>
    /// The build failed because an artifact does not exist in the repositories (P01 §4.4 step 5): <c>BLOCKED</c> with
    /// <c>B1_DEPENDENCY_RESOLUTION</c> rather than a compile failure.
    /// </summary>
    public static bool IsMissingArtifact(string output) =>
        output.Contains("Could not find artifact", StringComparison.Ordinal)
        || output.Contains("Could not resolve dependencies", StringComparison.Ordinal)
        || output.Contains("Could not resolve all files for configuration", StringComparison.Ordinal)
        || output.Contains("Could not find ", StringComparison.Ordinal) && output.Contains("Searched in the following locations", StringComparison.Ordinal);

    // from ARROW_Framework/src/Arrow.Service/External/BuildRunner.cs
    [GeneratedRegex(@"(?:error reading|Invalid or corrupt jarfile|error: error reading)\s+(?<jar>\S+?\.jar)\b(?:;|\s|$)", RegexOptions.Multiline)]
    private static partial Regex CorruptJar();

    /// <summary>
    /// javac or the JVM could not read a jar (<c>zip END header not found</c>, <c>error in opening zip file</c>): usually an HTML page
    /// that a repository returned instead of the file and that was saved as <c>.jar</c> (P01 §6.2 step 6). Deletes those jars when they
    /// are inside one of <paramref name="cacheDirs"/> (such as <c>~/.m2/repository</c>) and are not valid zip files; nothing else is
    /// deleted. Returns the number of files deleted.
    /// </summary>
    public static int RemoveCorruptCachedJars(string output, IReadOnlyList<string> cacheDirs)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var caches = cacheDirs.Select(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c)) + Path.DirectorySeparatorChar).ToList();
        var removed = 0;
        foreach (var jar in CorruptJar().Matches(output).Select(m => m.Groups["jar"].Value).Distinct(StringComparer.Ordinal))
        {
            string full;
            try
            {
                full = Path.GetFullPath(jar);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!caches.Any(c => full.StartsWith(c, comparison)) || !File.Exists(full) || IsZip(full))
            {
                continue;
            }

            File.Delete(full);
            removed++;
        }

        return removed;
    }

    /// <summary>Build caches of the user: <c>~/.m2/repository</c> and <c>~/.gradle/caches</c>.</summary>
    public static IReadOnlyList<string> UserCaches(string userProfile) =>
        [Path.Combine(userProfile, ".m2", "repository"), Path.Combine(userProfile, ".gradle", "caches")];

    private static bool IsZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count >= 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"Apache Maven (\d+(\.\d+)*)")]
    private static partial Regex MavenVersionLine();

    [GeneratedRegex(@"^Gradle (\d+(\.\d+)*)", RegexOptions.Multiline)]
    private static partial Regex GradleVersionLine();

    /// <summary>Version in the output of <c>mvn -v</c> or <c>gradle -v</c>; null when not found.</summary>
    public static string? ToolVersion(string output) =>
        MavenVersionLine().Match(output) is { Success: true } maven ? maven.Groups[1].Value
        : GradleVersionLine().Match(output) is { Success: true } gradle ? gradle.Groups[1].Value
        : null;

    /// <summary>
    /// First error line of a failed build, shown in <c>outcome.json</c> (P01 §8): a javac error (<c>[ERROR] …java:[3,24] …</c> or
    /// <c>…java:3: error: …</c>), the first lines under Gradle's <c>* What went wrong:</c>, otherwise the first <c>[ERROR]</c> line, otherwise the last line.
    /// </summary>
    public static string? FirstError(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        // Gradle: the lines under "* What went wrong:" say more than the last line ("BUILD FAILED in 5s").
        var wentWrong = lines.FindIndex(l => l == "* What went wrong:");
        if (wentWrong >= 0 && !lines.Any(l => l.Contains(".java:", StringComparison.Ordinal) && l.Contains("error:", StringComparison.Ordinal)))
        {
            return string.Join(" ", lines.Skip(wentWrong + 1).Take(3).Select(l => l.Trim().TrimStart('>').Trim()));
        }

        return lines.FirstOrDefault(l => l.Contains(".java:[", StringComparison.Ordinal) || l.Contains(".java:", StringComparison.Ordinal) && l.Contains("error:", StringComparison.Ordinal))
            ?? lines.FirstOrDefault(l => l.StartsWith("[ERROR]", StringComparison.Ordinal) && l.Length > "[ERROR] ".Length)
            ?? lines.LastOrDefault();
    }
}
