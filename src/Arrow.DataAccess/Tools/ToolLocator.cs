namespace Arrow.DataAccess.Tools;

/// <summary>
/// Finds programs as absolute paths (P01 §4.2, P08 §2.6). On Windows, a program started by its bare name from .NET (such as
/// <c>mvn.cmd</c>) may exit 1 without output, so every build tool is started by its absolute path.
/// </summary>
public sealed class ToolLocator(Func<string, string?> getEnvironment, bool? isWindows = null)
{
    private readonly bool _windows = isWindows ?? OperatingSystem.IsWindows();

    /// <summary>
    /// The first match of <paramref name="name"/> in <c>PATH</c>, trying the <c>PATHEXT</c> extensions on Windows
    /// (<c>mvn</c> → <c>mvn.cmd</c>). Null when not found.
    /// </summary>
    public string? FindOnPath(string name)
    {
        var path = getEnvironment("PATH") ?? getEnvironment("Path") ?? "";
        var extensions = _windows
            ? (getEnvironment("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = dir.Trim('"');
            if (_windows && Path.HasExtension(name) && File.Exists(Path.Combine(folder, name)))
            {
                return Path.GetFullPath(Path.Combine(folder, name));
            }

            foreach (var candidate in _windows ? extensions.Select(e => name + e.ToLowerInvariant()) : [name])
            {
                var full = Path.Combine(folder, candidate);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A configured program (<c>java.maven_path</c> and the like): a file is used as it is; a folder means its <c>bin/</c> program.
    /// Without a configured path, <paramref name="name"/> is looked up in <c>PATH</c>. Null when nothing exists.
    /// </summary>
    public string? Resolve(string? configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(full))
            {
                return full;
            }

            foreach (var dir in new[] { Path.Combine(full, "bin"), full })
            {
                foreach (var candidate in _windows ? [name + ".cmd", name + ".bat", name + ".exe"] : new[] { name })
                {
                    if (File.Exists(Path.Combine(dir, candidate)))
                    {
                        return Path.Combine(dir, candidate);
                    }
                }
            }

            return null;
        }

        return FindOnPath(name);
    }

    /// <summary>Wrapper file name of a build tool on this platform.</summary>
    public string WrapperName(bool gradle) => gradle ? (_windows ? "gradlew.bat" : "gradlew") : (_windows ? "mvnw.cmd" : "mvnw");
}
