using System.Text;
using Arrow.Common.Errors;
using Arrow.DataAccess.Apply;

namespace Arrow.DataAccess.Tests.Apply;

/// <summary>The writer of P06 §5 on a real temporary folder (P06 §9).</summary>
public sealed class ProjectFileWriterTests : IDisposable
{
    private const string Path1 = "src/test/java/com/acme/X_ArrowTest.java";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arrow-writer", Guid.NewGuid().ToString("N")[..8]);
    private readonly string _project;

    public ProjectFileWriterTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, "src", "test", "java"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<string> WriteAsync(string path, string content = "class X {}\r\n\r\n\n", LineEndings eol = LineEndings.Lf) =>
        new ProjectFileWriter().WriteNewAsync(_project, path, content, eol, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(LineEndings.Lf, "class X {\n  int a;\n}\n")]
    [InlineData(LineEndings.Crlf, "class X {\r\n  int a;\r\n}\r\n")]
    public async Task WritesANewFileWithTheLineEndingsOfTheCut(LineEndings eol, string expected)
    {
        var written = await WriteAsync(Path1, "class X {\r\n  int a;\n}\n\n\n", eol);
        Assert.Equal(Path.Combine(_project, "src", "test", "java", "com", "acme", "X_ArrowTest.java"), written);
        var bytes = await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(written)!, "*.arrow-tmp", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public void LineEndingsFollowTheCut()
    {
        Assert.Equal(LineEndings.Crlf, ProjectFileWriter.EndingsOf("package a;\r\nclass B {}\r\n"));
        Assert.Equal(LineEndings.Lf, ProjectFileWriter.EndingsOf("package a;\nclass B {}\n"));
    }

    [Fact]
    public async Task NeverOverwrites()
    {
        var existing = Path.Combine(_project, "src", "test", "java", "com", "acme", "X_ArrowTest.java");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "the user's test", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<ArrowException>(() => WriteAsync(Path1));
        Assert.Equal(ErrorCodes.WTargetExists, error.Code);
        Assert.Equal("the user's test", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(existing)!, "*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Theory]
    [InlineData("../outside/X_ArrowTest.java")]
    [InlineData("src/test/java/../../../../outside/X_ArrowTest.java")]
    [InlineData("src//test/X_ArrowTest.java")]
    [InlineData("C:/outside/X_ArrowTest.java")]
    [InlineData("/outside/X_ArrowTest.java")]
    public async Task PathsOutsideTheProjectAreRefused(string path)
    {
        var error = await Assert.ThrowsAsync<ArrowException>(() => WriteAsync(path));
        Assert.Equal(ErrorCodes.WTargetOutsideProject, error.Code);
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Fact]
    public async Task ALinkOutOfTheProjectIsRefused()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_project, "src", "test", "java", "com");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("creating a symbolic link needs a privilege on this machine: " + ex.Message);
        }

        var error = await Assert.ThrowsAsync<ArrowException>(() => WriteAsync(Path1));
        Assert.Equal(ErrorCodes.WTargetOutsideProject, error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }
}
