using System.Globalization;
using Arrow.Business.Environment;
using Arrow.Common.Analysis;
using Arrow.Common.Config;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.Tools;

namespace Arrow.Business.UseCases;

/// <summary>Paths the user sets in step 4 of <c>arrow setup</c>; an absent entry keeps what the configuration says.</summary>
/// <param name="JdkHomes">JDK home by major (<c>"8"</c>, <c>"11"</c>, <c>"17"</c>, <c>"21"</c>).</param>
/// <param name="MavenPath">Maven executable; null keeps the configuration.</param>
/// <param name="GradlePath">Gradle executable; null keeps the configuration.</param>
public sealed record MachineChoice(IReadOnlyDictionary<string, string> JdkHomes, string? MavenPath, string? GradlePath)
{
    /// <summary>Nothing changed.</summary>
    public static MachineChoice None { get; } = new(new Dictionary<string, string>(), null, null);

    /// <summary>True when the user set at least one path.</summary>
    public bool IsEmpty => JdkHomes.Count == 0 && MavenPath is null && GradlePath is null;
}

/// <summary>One JDK major of the table of step 4.</summary>
/// <param name="Major">8, 11, 17 or 21.</param>
/// <param name="Version">Full version; null when none was found.</param>
/// <param name="Home">JDK home; null when none was found.</param>
/// <param name="Source">Where it was found (<c>config</c>, <c>JAVA_HOME</c>, a folder…).</param>
public sealed record MachineJdk(int Major, string? Version, string? Home, string? Source);

/// <summary>What step 4 shows (P08 §2.5): the JDKs ARROW supports, Maven and Gradle, and the configured homes that do not work.</summary>
/// <param name="Jdks">One row per supported major.</param>
/// <param name="Maven">Maven executable; null when not found.</param>
/// <param name="Gradle">Gradle executable; null when not found.</param>
/// <param name="Broken">Configured JDK homes where <c>java -version</c> failed.</param>
public sealed record MachineFound(IReadOnlyList<MachineJdk> Jdks, string? Maven, string? Gradle, IReadOnlyList<string> Broken);

/// <summary>Step 4 of <c>arrow setup</c>: find the JDKs and the build tools, and save the paths the user corrected.</summary>
public interface IMachineSetupUseCase
{
    /// <summary>Finds the tools with <paramref name="choice"/> on top of the configuration.</summary>
    Task<MachineFound> FindAsync(MachineChoice choice, string? projectRoot, CancellationToken ct);

    /// <summary>Writes the paths of <paramref name="choice"/> into the user <c>config.json</c>.</summary>
    void Save(MachineChoice choice, string? projectRoot);
}

/// <summary><see cref="IMachineSetupUseCase"/> over <see cref="ToolDiscovery"/> and <see cref="ConfigUseCase"/>.</summary>
public sealed class MachineSetupUseCase(IConfigStore config, ConfigUseCase configUseCase, ToolDiscovery tools) : IMachineSetupUseCase
{
    private static readonly Dictionary<string, string?> NoOverrides = [];

    public async Task<MachineFound> FindAsync(MachineChoice choice, string? projectRoot, CancellationToken ct)
    {
        var java = config.Load(projectRoot, NoOverrides).Config.Java;
        var homes = java.JdkHomes.ToDictionary(StringComparer.Ordinal);
        foreach (var (major, home) in choice.JdkHomes)
        {
            homes[major] = home;
        }

        var machine = await tools.FindAsync(java with
        {
            JdkHomes = homes,
            MavenPath = choice.MavenPath ?? java.MavenPath,
            GradlePath = choice.GradlePath ?? java.GradlePath,
        }, ct).ConfigureAwait(false);
        var byMajor = JdkLocator.ByMajor(machine.Jdks);
        var rows = JavaVersions.Supported.Order()
            .Select(m => byMajor.TryGetValue(m, out var jdk) ? new MachineJdk(m, jdk.Version, jdk.Home, jdk.Source) : new MachineJdk(m, null, null, null))
            .ToList();
        return new MachineFound(rows, machine.Maven, machine.Gradle, machine.Jdks.Where(j => j.Major == 0).Select(j => j.Home).ToList());
    }

    public void Save(MachineChoice choice, string? projectRoot)
    {
        foreach (var (major, home) in choice.JdkHomes.OrderBy(j => int.Parse(j.Key, CultureInfo.InvariantCulture)))
        {
            configUseCase.Set(ConfigScope.User, "java.jdk_homes." + major, home, projectRoot);
        }

        if (choice.MavenPath is { } maven)
        {
            configUseCase.Set(ConfigScope.User, "java.maven_path", maven, projectRoot);
        }

        if (choice.GradlePath is { } gradle)
        {
            configUseCase.Set(ConfigScope.User, "java.gradle_path", gradle, projectRoot);
        }
    }
}
