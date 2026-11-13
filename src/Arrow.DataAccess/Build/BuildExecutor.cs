using Arrow.DataAccess.Processes;

namespace Arrow.DataAccess.Build;

/// <summary>A finished build command.</summary>
/// <param name="Command">Program and arguments.</param>
/// <param name="WorkingDirectory">Folder it ran in.</param>
/// <param name="Process">Exit code, output and time.</param>
/// <param name="Failure">Classification of a failure; <see cref="BuildFailureKind.None"/> on success.</param>
/// <param name="RemovedCorruptJars">Corrupt cached jars deleted after this command; the failure is then a network one, worth a retry.</param>
public sealed record BuildResult(IReadOnlyList<string> Command, string WorkingDirectory, ProcessResult Process, BuildFailureKind Failure, int RemovedCorruptJars);

/// <summary>Runs Maven and Gradle commands (P01 §10).</summary>
public interface IBuildExecutor
{
    /// <summary>
    /// Runs <paramref name="arguments"/> for <paramref name="setup"/> on the copy of the project at <paramref name="projectRoot"/>,
    /// with the JDK at <paramref name="jdkHome"/>. Writes the P00 §5 log to <paramref name="logFile"/> when given.
    /// </summary>
    Task<BuildResult> RunAsync(BuildSetup setup, string projectRoot, IReadOnlyList<string> arguments, string jdkHome, TimeSpan timeout, string? logFile, CancellationToken ct);
}

/// <summary>
/// <see cref="IBuildExecutor"/> on <see cref="IProcessRunner"/>. The environment of P01 §6: <c>JAVA_HOME</c> and <c>PATH</c> of the
/// chosen JDK, the fixed <c>JAVA_TOOL_OPTIONS</c>, <c>MAVEN_OPTS</c>/<c>GRADLE_OPTS</c> <c>-Xmx1g</c> unless the user set them
/// (P07 §7), and no <c>MAVEN_ARGS</c>/<c>JDK_JAVA_OPTIONS</c> that would change the commands.
/// </summary>
/// <param name="runner">Starts processes.</param>
/// <param name="getEnvironment">Reads the environment of ARROW (for <c>PATH</c> and the user's options).</param>
/// <param name="buildCaches">Folders where corrupt jars may be deleted, normally <see cref="BuildFailureClassifier.UserCaches"/>.</param>
public sealed class BuildExecutor(IProcessRunner runner, Func<string, string?> getEnvironment, IReadOnlyList<string> buildCaches) : IBuildExecutor
{
    public async Task<BuildResult> RunAsync(BuildSetup setup, string projectRoot, IReadOnlyList<string> arguments, string jdkHome, TimeSpan timeout, string? logFile, CancellationToken ct)
    {
        var command = BuildCommands.Command(setup, projectRoot, arguments);
        var workingDirectory = setup.WorkingDirectory(projectRoot);
        var result = await runner.RunAsync(
            new ProcessSpec(command[0], command.Skip(1).ToList(), workingDirectory, Environment(jdkHome), timeout, LogFile: logFile),
            ct).ConfigureAwait(false);

        var failure = BuildFailureClassifier.Classify(result);
        var removed = 0;
        if (failure == BuildFailureKind.Build)
        {
            // A corrupt jar in ~/.m2 or ~/.gradle is a fault of the machine: once deleted, a retry downloads it again (P01 §6.2 step 6).
            removed = BuildFailureClassifier.RemoveCorruptCachedJars(result.Output, buildCaches);
            if (removed > 0)
            {
                failure = BuildFailureKind.Network;
            }
        }

        return new BuildResult(command, workingDirectory, result, failure, removed);
    }

    /// <summary>Environment overrides for a build with the JDK at <paramref name="jdkHome"/>.</summary>
    public IReadOnlyDictionary<string, string?> Environment(string jdkHome) => new Dictionary<string, string?>
    {
        ["JAVA_HOME"] = jdkHome,
        ["PATH"] = Path.Combine(jdkHome, "bin") + Path.PathSeparator + (getEnvironment("PATH") ?? getEnvironment("Path") ?? ""),
        ["JAVA_TOOL_OPTIONS"] = BuildCommands.JavaToolOptions,
        ["JDK_JAVA_OPTIONS"] = null,
        ["_JAVA_OPTIONS"] = null,
        ["MAVEN_ARGS"] = null,
        ["MAVEN_OPTS"] = getEnvironment("MAVEN_OPTS") is { Length: > 0 } maven ? maven : "-Xmx1g",
        ["GRADLE_OPTS"] = getEnvironment("GRADLE_OPTS") is { Length: > 0 } gradle ? gradle : "-Xmx1g",
    };
}
