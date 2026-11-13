using System.Globalization;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.Environment;

/// <summary>JDKs, git, Maven and Gradle found on the machine (P01 §4.2, P08 §2.6).</summary>
/// <param name="Jdks">Every JDK found, configured ones first; a configured home that does not work has major 0.</param>
/// <param name="Git">Absolute path of git; null when not found.</param>
/// <param name="Maven">Absolute path of Maven; null when not found.</param>
/// <param name="Gradle">Absolute path of Gradle; null when not found.</param>
public sealed record MachineTools(IReadOnlyList<JdkInstall> Jdks, string? Git, string? Maven, string? Gradle)
{
    /// <summary>JDK homes by major for builds: only the majors ARROW supports (P01 §1).</summary>
    public IReadOnlyDictionary<int, string> BuildJdkHomes =>
        JdkLocator.ByMajor(Jdks).Where(j => JavaVersions.Supported.Contains(j.Key)).ToDictionary(j => j.Key, j => j.Value.Home);

    /// <summary>JDK homes by major that can run java-helper (17 or newer, any version).</summary>
    public IReadOnlyDictionary<string, string> HelperJdkHomes =>
        JdkLocator.ByMajor(Jdks).ToDictionary(j => j.Key.ToString(CultureInfo.InvariantCulture), j => j.Value.Home);
}

/// <summary>Finds the tools of the machine, honouring <c>java.*</c> of the configuration.</summary>
public sealed class ToolDiscovery(IProcessRunner runner, Func<string, string?> getEnvironment, string userProfile, bool? isWindows = null, IReadOnlyList<string>? installFolders = null)
{
    /// <summary>JDKs from <c>java.jdk_homes</c>, the environment and the usual folders; git, Maven and Gradle from their configured path or <c>PATH</c>.</summary>
    public async Task<MachineTools> FindAsync(JavaSettings java, CancellationToken ct)
    {
        var jdks = await new JdkLocator(runner, getEnvironment, userProfile, isWindows, installFolders).FindAsync(java.JdkHomes, ct).ConfigureAwait(false);
        var locator = new ToolLocator(getEnvironment, isWindows);
        return new MachineTools(jdks, locator.Resolve(java.GitPath, "git"), locator.Resolve(java.MavenPath, "mvn"), locator.Resolve(java.GradlePath, "gradle"));
    }
}
