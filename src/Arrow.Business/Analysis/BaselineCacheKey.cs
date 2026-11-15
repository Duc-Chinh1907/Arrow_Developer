using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Arrow.Business.Analysis;

/// <summary>What decides the outcome of a baseline (P01 §6.2), so that a cached one is reused only when all of it is unchanged.</summary>
/// <param name="Code">The code: <c>source_tree_hash</c> of a folder, or the commit of a repository.</param>
/// <param name="BuildRoot">Build root, relative to the project root.</param>
/// <param name="Mode">Standalone or reactor.</param>
/// <param name="Selector">Maven module selector or Gradle project.</param>
/// <param name="Tool">Maven or Gradle, the program used (wrapper or installed) and its version.</param>
/// <param name="Jdk">Major and home of the JDK.</param>
/// <param name="ModuleTest">The module test command.</param>
/// <param name="Classpath">Resolved test classpath; a jar adds its size and time, so a changed <c>SNAPSHOT</c> jar changes the key.</param>
/// <param name="Runs"><c>verification.baseline_runs</c>.</param>
public sealed record BaselineCacheInputs(
    string Code,
    string BuildRoot,
    string Mode,
    string Selector,
    string Tool,
    string Jdk,
    IReadOnlyList<string> ModuleTest,
    IReadOnlyList<string> Classpath,
    int Runs);

/// <summary>The key of the baseline cache: a sha256 of <see cref="BaselineCacheInputs"/> and the version of ARROW.</summary>
public static class BaselineCacheKey
{
    /// <summary>How long a cached baseline is used (P01 §6.2).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Version of ARROW that measures and reads the cache; a new version measures again.</summary>
    public static string ArrowVersion { get; } =
        typeof(BaselineCacheKey).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The key, 64 lowercase hex characters.</summary>
    public static string Compute(BaselineCacheInputs inputs, Func<string, (long Length, DateTime WrittenUtc)?>? jarStamp = null)
    {
        jarStamp ??= Stamp;
        var text = new StringBuilder()
            .AppendLine("arrow " + ArrowVersion)
            .AppendLine("code " + inputs.Code)
            .AppendLine("root " + inputs.BuildRoot)
            .AppendLine("mode " + inputs.Mode)
            .AppendLine("selector " + inputs.Selector)
            .AppendLine("tool " + inputs.Tool)
            .AppendLine("jdk " + inputs.Jdk)
            .AppendLine("test " + string.Join(' ', inputs.ModuleTest))
            .AppendLine("runs " + inputs.Runs.ToString(CultureInfo.InvariantCulture));
        foreach (var entry in inputs.Classpath)
        {
            text.Append("cp ").Append(entry);
            if (entry.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && jarStamp(entry) is { } stamp)
            {
                text.Append(' ').Append(stamp.Length.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(stamp.WrittenUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            text.AppendLine();
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Whether an entry measured at <paramref name="measuredAt"/> can still be used at <paramref name="now"/>.</summary>
    public static bool Fresh(DateTimeOffset measuredAt, DateTimeOffset now) => now - measuredAt <= MaxAge && measuredAt <= now + TimeSpan.FromMinutes(5);

    private static (long, DateTime)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
