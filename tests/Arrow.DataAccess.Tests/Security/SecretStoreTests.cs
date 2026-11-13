using Arrow.Common.Secrets;
using Arrow.DataAccess.Security;

namespace Arrow.DataAccess.Tests.Security;

/// <summary>API keys (P09 §4). Uses the fake key of CLAUDE.md, never a real one.</summary>
public class SecretStoreTests
{
    private const string FakeKey = "sk-test-SECRET-123";

    [Fact]
    public void StoredKeyRoundTripsAndIsNotPlainTextOnWindows()
    {
        using var temp = new TempDir();
        var store = Store(temp);
        store.Set("teamo", new SecretValue(FakeKey));

        Assert.Equal(FakeKey, store.Get("teamo")!.Reveal());
        var file = Assert.Single(Directory.GetFiles(temp.PathOf("secrets")));
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith(".bin", file, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", File.ReadAllText(file), StringComparison.Ordinal);
        }
        else
        {
            Assert.EndsWith(".key", file, StringComparison.Ordinal);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(temp.PathOf("secrets")));
        }
    }

    [Fact]
    public void EnvironmentComesBeforeTheStore()
    {
        using var temp = new TempDir();
        var env = new Dictionary<string, string?> { ["ARROW_SECRET_TEAMO"] = "sk-test-FROM-ENV-456", ["OPENAI_API_KEY"] = "sk-test-OPENAI-789" };
        var store = Store(temp, env);
        store.Set("teamo", new SecretValue(FakeKey));
        store.Set("openai", new SecretValue(FakeKey));

        Assert.Equal("sk-test-FROM-ENV-456", store.Get("teamo")!.Reveal());
        Assert.Equal("sk-test-OPENAI-789", store.Get("openai")!.Reveal());
        Assert.Null(store.Get("anthropic"));
        Assert.Equal("ARROW_SECRET_MY_PROXY", SecretStore.EnvironmentVariable("my-proxy"));
    }

    [Fact]
    public void ListShowsOnlyTheLastCharacters()
    {
        using var temp = new TempDir();
        var store = Store(temp, new Dictionary<string, string?> { ["ARROW_SECRET_LOCAL_PROXY"] = "sk-test-LOCAL-0000" });
        store.Set("teamo", new SecretValue(FakeKey));

        var list = store.List();
        Assert.Equal(
            [new SecretInfo("local-proxy", "...0000", SecretSource.ArrowEnvironment), new SecretInfo("teamo", "...-123", SecretSource.Store)],
            list);
        Assert.DoesNotContain(list, s => s.Hint.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveDeletesTheStoredKey()
    {
        using var temp = new TempDir();
        var store = Store(temp);
        store.Set("teamo", new SecretValue(FakeKey));
        Assert.True(store.Remove("teamo"));
        Assert.False(store.Remove("teamo"));
        Assert.Null(store.Get("teamo"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Teamo")]
    [InlineData("a_b")]
    [InlineData("")]
    public void ReferencesAreSafeFileNames(string reference)
    {
        using var temp = new TempDir();
        Assert.Throws<ArgumentException>(() => Store(temp).Get(reference));
    }

    [Fact]
    public void KeysAreRegisteredForRedaction()
    {
        using var temp = new TempDir();
        Store(temp).Set("teamo", new SecretValue("sk-test-REDACT-me-31337"));
        Assert.Equal("x *** y", Redactor.Redact("x sk-test-REDACT-me-31337 y"));
    }

    [Fact]
    public void SecretValueNeverPrintsItself()
    {
        var value = new SecretValue(FakeKey);
        Assert.Equal("***", value.ToString());
        Assert.Equal("***", $"{value}");
        Assert.Equal("...-123", value.Hint);
    }

    private static SecretStore Store(TempDir temp, Dictionary<string, string?>? env = null)
    {
        env ??= [];
        return new SecretStore(temp.PathOf("secrets"), name => env.GetValueOrDefault(name), () => env.Keys);
    }
}
