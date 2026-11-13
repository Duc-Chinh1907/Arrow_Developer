using Arrow.DataAccess.Source;

namespace Arrow.DataAccess.Tests.Source;

/// <summary>Copying a local project (P01 §3.1, §11): filters, <c>source_tree_hash</c>, links, project root.</summary>
public class LocalProjectTreeTests
{
    private static void Project(TempDir temp)
    {
        temp.Write("pom.xml", "<project/>");
        temp.Write("src/main/java/a/Order.java", "package a; class Order {}");
        temp.Write("src/main/java/a/build/Step.java", "package a.build; class Step {}");
        temp.Write("src/main/java/a/out/Sink.java", "package a.out; class Sink {}");
        temp.Write("target/classes/a/Order.class", "x");
        temp.Write("core/pom.xml", "<project/>");
        temp.Write("core/build/tmp.txt", "x");
        temp.Write("core/bin/run.txt", "x");
        temp.Write("tools/bin/run.sh", "echo");
        temp.Write(".git/HEAD", "ref: refs/heads/main");
        temp.Write(".idea/workspace.xml", "x");
        temp.Write("web/node_modules/x/index.js", "x");
        temp.Write(".gradle/cache", "x");
        temp.Write("build.log", "x");
        temp.Write("lib/Old.class", "x");
        temp.Write("README.md", "hello");
    }

    [Fact]
    public void CopiesWithTheFiltersOfP01()
    {
        using var temp = new TempDir();
        Project(temp);
        var tree = LocalProjectTree.Scan(temp.Root);
        Assert.Equal(
        [
            "README.md", "core/pom.xml", "pom.xml", "src/main/java/a/Order.java", "src/main/java/a/build/Step.java", "src/main/java/a/out/Sink.java",
            "tools/bin/run.sh",
        ], tree.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task CopyKeepsContentAndLeavesTheOriginalUntouched()
    {
        using var temp = new TempDir();
        Project(temp);
        var project = Path.Combine(temp.Root, "project");
        Directory.CreateDirectory(project);
        foreach (var file in Directory.GetFiles(temp.Root, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(project, Path.GetRelativePath(temp.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var before = LocalProjectTree.Scan(project).SourceTreeHash;
        var copy = Path.Combine(temp.Root, "copy");
        await LocalProjectTree.Scan(project).CopyAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal("package a; class Order {}", File.ReadAllText(Path.Combine(copy, "src", "main", "java", "a", "Order.java")));
        Assert.False(Directory.Exists(Path.Combine(copy, "target")));
        Assert.Equal(before, LocalProjectTree.Scan(project).SourceTreeHash);
        await Assert.ThrowsAsync<IOException>(() => LocalProjectTree.Scan(project).CopyAsync(copy, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HashDependsOnContentNotOnListingOrder()
    {
        var time = new DateTime(2026, 11, 10, 7, 0, 0, DateTimeKind.Utc);
        TreeFile a = new("a/A.java", 10, time), b = new("b/B.java", 20, time);
        var one = new LocalProjectTree("/p", [a, b], [], []);
        var two = new LocalProjectTree("/p", [b, a], [], []);
        Assert.Equal(one.SourceTreeHash, two.SourceTreeHash);
        Assert.StartsWith("sha256:", one.SourceTreeHash, StringComparison.Ordinal);
        Assert.NotEqual(one.SourceTreeHash, new LocalProjectTree("/p", [a, b with { Size = 21 }], [], []).SourceTreeHash);
        Assert.NotEqual(one.SourceTreeHash, new LocalProjectTree("/p", [a, b with { ModifiedUtc = time.AddSeconds(1) }], [], []).SourceTreeHash);
        Assert.NotEqual(one.SourceTreeHash, new LocalProjectTree("/p", [a], [], []).SourceTreeHash);
        Assert.Equal(30, one.TotalBytes);
    }

    [Fact]
    public void LinksInsideAreKeptAndLinksOutsideSkipped()
    {
        using var temp = new TempDir();
        var project = Path.Combine(temp.Root, "p");
        temp.Write("p/pom.xml", "<project/>");
        temp.Write("p/docs/guide.md", "x");
        temp.Write("outside/secret.txt", "x");
        try
        {
            File.CreateSymbolicLink(Path.Combine(project, "guide-link.md"), Path.Combine("docs", "guide.md"));
            File.CreateSymbolicLink(Path.Combine(project, "secret-link.txt"), Path.Combine(temp.Root, "outside", "secret.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links needs developer mode or admin rights on this machine: " + ex.Message);
        }

        var tree = LocalProjectTree.Scan(project);
        Assert.Equal(["guide-link.md"], tree.Links.Select(l => l.Path));
        Assert.Contains(tree.Warnings, w => w.Contains("secret-link.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(tree.Files, f => f.Path.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsTheProjectRoot()
    {
        using var temp = new TempDir();
        temp.Write("repo/lib/pom.xml", "<project/>");
        temp.Write("repo/lib/src/main/java/a/A.java", "");
        temp.Write("multi/pom.xml", "<project/>");
        temp.Write("multi/core/pom.xml", "<project/>");
        temp.Write("multi/core/src/main/java/a/B.java", "");

        string P(string relative) => Path.GetFullPath(Path.Combine(temp.Root, relative));
        Assert.Equal(P("repo/lib"), ProjectRootFinder.Find(P("repo/lib")));
        Assert.Equal(P("repo/lib"), ProjectRootFinder.Find(P("repo/lib/src/main/java/a")));
        // From inside a module, the highest of the consecutive folders with a build file.
        Assert.Equal(P("multi"), ProjectRootFinder.Find(P("multi/core/src/main/java")));
        // A folder that has a build file is the root itself.
        Assert.Equal(P("multi/core"), ProjectRootFinder.Find(P("multi/core")));
        // No build file at or above it, but one in lib/: the folder itself is the root (maven-subdir-junit4).
        Assert.Equal(P("repo"), ProjectRootFinder.Find(P("repo")));
        Directory.CreateDirectory(P("empty/a"));
        Assert.Null(ProjectRootFinder.Find(P("empty/a")));
        Assert.Null(ProjectRootFinder.Find(P("missing")));
    }
}
