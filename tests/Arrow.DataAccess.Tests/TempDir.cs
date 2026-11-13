namespace Arrow.DataAccess.Tests;

/// <summary>A fresh folder under the system temp folder, deleted when the test ends.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        // A short ASCII path without spaces, like a valid ARROW_HOME (P09 §2).
        Root = Path.Combine(Path.GetTempPath(), "arrow-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Write(string relativePath, string text)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
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
}
