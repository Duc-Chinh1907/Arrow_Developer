using System.Globalization;
using System.Security.Cryptography;
using Arrow.Common.Errors;
using Arrow.DataAccess.Storage;

namespace Arrow.DataAccess.JavaHelper;

/// <summary>
/// The java-helper jar shipped inside <c>Arrow.DataAccess</c> (P10 §2) and the JDK that runs it (P10 §3.1).
/// The jar is built by <c>mvn -f tools/java-helper/pom.xml package</c> and embedded when the .NET build finds it.
/// </summary>
public static class JavaHelperJar
{
    /// <summary>Version of the embedded jar; equal to <c>Main.VERSION</c> of java-helper.</summary>
    public const string Version = "1.1.0";

    /// <summary>Lowest JDK major that runs java-helper.</summary>
    public const int MinimumJdk = 17;

    internal const string ResourceName = "Arrow.DataAccess.JavaHelper.java-helper.jar";

    /// <summary>True when this build of ARROW carries the jar.</summary>
    public static bool IsEmbedded => typeof(JavaHelperJar).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>
    /// Extracts the jar to <c>ARROW_HOME/tools/java-helper-&lt;version&gt;.jar</c> unless a file with the same sha256 is
    /// already there (P10 §2), and returns its path.
    /// </summary>
    public static string Ensure(ArrowHome home)
    {
        using var stream = typeof(JavaHelperJar).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new ArrowException(ErrorCodes.JhStartFailed,
                "Bản build này không có java-helper.jar. Chạy \"mvn -q -f tools/java-helper/pom.xml package\" rồi build lại.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var path = Path.Combine(home.ToolsDir, $"java-helper-{Version}.jar");
        if (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            return path;
        }

        IO.AtomicFile.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// The <c>java</c> of the smallest JDK with major ≥ 17 among <paramref name="jdkHomes"/> (major → home), or
    /// <c>JH_JDK_MISSING</c> (P10 §3.1).
    /// </summary>
    public static string FindJava(IReadOnlyDictionary<string, string> jdkHomes)
    {
        var java = jdkHomes
            .Select(e => (Major: int.TryParse(e.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0, Home: e.Value))
            .Where(e => e.Major >= MinimumJdk)
            .OrderBy(e => e.Major)
            .Select(e => Path.Combine(e.Home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"))
            .FirstOrDefault(File.Exists);
        return java ?? throw new ArrowException(ErrorCodes.JhJdkMissing);
    }
}
