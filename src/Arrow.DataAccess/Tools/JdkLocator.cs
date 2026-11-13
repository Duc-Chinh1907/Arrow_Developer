using System.Globalization;
using System.Runtime.Versioning;
using Arrow.Common.Analysis;
using Arrow.DataAccess.Processes;

namespace Arrow.DataAccess.Tools;

/// <summary>A JDK found on the machine.</summary>
/// <param name="Major">Major version, e.g. 17.</param>
/// <param name="Version">Full version, e.g. <c>17.0.12</c>.</param>
/// <param name="Home">JDK home folder.</param>
/// <param name="Source">Where it was found: <c>config</c>, <c>JAVA_HOME</c>, <c>PATH</c>, a folder or the registry.</param>
public sealed record JdkInstall(int Major, string Version, string Home, string Source);

/// <summary>
/// Finds the JDKs of the machine (P08 §2.6): <c>java.jdk_homes</c>, the <c>JAVA_HOME_&lt;n&gt;_X64</c> variables (CI runners and this
/// project's machines), <c>JAVA_HOME</c>, <c>PATH</c>, the usual install folders and, on Windows, the registry. The version comes from
/// running <c>java</c>. A folder without <c>javac</c> is a JRE and is skipped.
/// </summary>
public sealed class JdkLocator(IProcessRunner runner, Func<string, string?> getEnvironment, string userProfile, bool? isWindows = null, IReadOnlyList<string>? installFolders = null)
{
    private readonly bool _windows = isWindows ?? OperatingSystem.IsWindows();

    /// <summary>
    /// Every JDK found, one per home folder, in the search order above. <paramref name="configured"/> is <c>java.jdk_homes</c>
    /// (major → home); a configured home that does not work is still reported, with major 0, so <c>doctor</c> can show it.
    /// </summary>
    public async Task<IReadOnlyList<JdkInstall>> FindAsync(IReadOnlyDictionary<string, string> configured, CancellationToken ct)
    {
        var found = new List<JdkInstall>();
        var seen = new HashSet<string>(_windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (home, source) in Candidates(configured))
        {
            var full = Normalize(home);
            if (full is null || !seen.Add(full))
            {
                continue;
            }

            if (await ProbeAsync(full, source, ct).ConfigureAwait(false) is { } jdk)
            {
                found.Add(jdk);
            }
            else if (source == "config")
            {
                found.Add(new JdkInstall(0, "", full, source));
            }
        }

        return found;
    }

    /// <summary>One JDK home per major: the first found for each major (configured ones first).</summary>
    public static IReadOnlyDictionary<int, JdkInstall> ByMajor(IEnumerable<JdkInstall> jdks)
    {
        var result = new SortedDictionary<int, JdkInstall>();
        foreach (var jdk in jdks.Where(j => j.Major > 0))
        {
            result.TryAdd(jdk.Major, jdk);
        }

        return result;
    }

    /// <summary>Runs <c>java</c> of <paramref name="home"/>; null when it is not a working JDK.</summary>
    public async Task<JdkInstall?> ProbeAsync(string home, string source, CancellationToken ct)
    {
        var java = Path.Combine(home, "bin", _windows ? "java.exe" : "java");
        var javac = Path.Combine(home, "bin", _windows ? "javac.exe" : "javac");
        if (!File.Exists(java) || !File.Exists(javac))
        {
            return null;
        }

        try
        {
            var result = await runner.RunAsync(
                new ProcessSpec(java, ["-version"], Path.GetTempPath(), new Dictionary<string, string?> { ["JAVA_TOOL_OPTIONS"] = null, ["_JAVA_OPTIONS"] = null }, TimeSpan.FromSeconds(30)),
                ct).ConfigureAwait(false);
            var major = JavaVersions.ParseRuntimeMajor(result.Output);
            return major is null ? null : new JdkInstall(major.Value, JavaVersions.ParseRuntimeVersion(result.Output) ?? "", home, source);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private IEnumerable<(string Home, string Source)> Candidates(IReadOnlyDictionary<string, string> configured)
    {
        foreach (var (_, home) in configured.OrderBy(e => int.TryParse(e.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0))
        {
            yield return (home, "config");
        }

        foreach (var major in new[] { 8, 11, 17, 21, 25 })
        {
            foreach (var arch in new[] { "X64", "ARM64" })
            {
                var name = $"JAVA_HOME_{major}_{arch}";
                if (getEnvironment(name) is { Length: > 0 } home)
                {
                    yield return (home, name);
                }
            }
        }

        if (getEnvironment("JAVA_HOME") is { Length: > 0 } javaHome)
        {
            yield return (javaHome, "JAVA_HOME");
        }

        if (new ToolLocator(getEnvironment, _windows).FindOnPath("java") is { } onPath && HomeOfJava(onPath) is { } pathHome)
        {
            yield return (pathHome, "PATH");
        }

        foreach (var dir in InstallFolders())
        {
            yield return (dir, "folder");
        }

        if (_windows && OperatingSystem.IsWindows())
        {
            foreach (var home in RegistryHomes())
            {
                yield return (home, "registry");
            }
        }
    }

    private IEnumerable<string> InstallFolders()
    {
        if (installFolders is not null)
        {
            // Given by the caller (tests): folders whose sub-folders are JDK homes; nothing else is searched.
            return installFolders.SelectMany(Children);
        }

        IEnumerable<string> parents;
        if (_windows)
        {
            var programFiles = getEnvironment("ProgramFiles") ?? @"C:\Program Files";
            parents = new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft" }.Select(v => Path.Combine(programFiles, v));
        }
        else if (OperatingSystem.IsMacOS())
        {
            return Children("/Library/Java/JavaVirtualMachines").Select(d => Path.Combine(d, "Contents", "Home"))
                .Concat(Children(Path.Combine(userProfile, ".sdkman", "candidates", "java")));
        }
        else
        {
            parents = ["/usr/lib/jvm"];
        }

        return parents.SelectMany(Children).Concat(Children(Path.Combine(userProfile, ".sdkman", "candidates", "java")));
    }

    private static IEnumerable<string> Children(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> RegistryHomes()
    {
        var homes = new List<string>();
        try
        {
            foreach (var keyName in new[] { @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\Java Development Kit" })
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyName);
                foreach (var version in key?.GetSubKeyNames() ?? [])
                {
                    using var sub = key!.OpenSubKey(version);
                    if (sub?.GetValue("JavaHome") is string home)
                    {
                        homes.Add(home);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable registry only means fewer candidates.
        }

        return homes;
    }

    /// <summary>
    /// JDK home of a <c>java</c> found in PATH: the folder above <c>bin/</c>, following links. Oracle's <c>javapath</c> shims are not a
    /// JDK home, so they give null.
    /// </summary>
    private static string? HomeOfJava(string java)
    {
        var file = new FileInfo(java);
        var real = file.LinkTarget is not null ? file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? java : java;
        var bin = Path.GetDirectoryName(real);
        return bin is not null && Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(bin) : null;
    }

    private static string? Normalize(string home)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(home.Trim().Trim('"')));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
