using System.Security.Cryptography;
using System.Text;

namespace Arrow.IntegrationTests.Fixtures;

/// <summary>
/// A temporary copy of a fixture of <c>tests/fixtures/</c>. P11 §2: tests never run on the fixture itself, and the
/// original must be unchanged afterwards (<see cref="FingerprintOf"/>).
/// </summary>
public sealed class FixtureRepo : IDisposable
{
    public FixtureRepo(string name)
    {
        Name = name;
        Source = Path.Combine(FixturesDir, name);
        Root = Path.Combine(Path.GetTempPath(), "arrow-fixtures", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Copy(Source, Root);
    }

    public string Name { get; }

    /// <summary>The fixture in the repository; read only.</summary>
    public string Source { get; }

    /// <summary>The working copy.</summary>
    public string Root { get; }

    /// <summary><c>tests/fixtures</c> of this repository.</summary>
    public static string FixturesDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ARROW.slnx")))
                {
                    return Path.Combine(dir.FullName, "tests", "fixtures");
                }
            }

            throw new InvalidOperationException("ARROW.slnx not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>sha256 over the relative paths and contents of every file, in a fixed order.</summary>
    public static string FingerprintOf(string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, file).Replace('\\', '/') + "\n"));
            sha.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }
    }
}
