using Arrow.Common.Secrets;
using Arrow.DataAccess.Security;

namespace Arrow.DataAccess.Tests.Security;

/// <summary>Keys never reach logs or files (P00 §5, P09 §4.5).</summary>
public class RedactorTests
{
    /// <summary>
    /// Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/RunRepositoryTests.RedactorPatterns</c>.
    /// Differs from the old code by the P-specs: masks are <c>***</c> in logs (P09 §4.5) and <c>...wxyz</c> on screen
    /// (P00 §5, <see cref="SecretValue.MaskForDisplay"/>) instead of <c>***wxyz</c>.
    /// </summary>
    [Fact]
    public void RedactorPatterns()
    {
        Assert.Equal("***", SecretValue.MaskForDisplay("abc"));
        Assert.Equal("...wxyz", SecretValue.MaskForDisplay("abcdefwxyz"));
        var text = Redactor.Redact("api_key: abc123secretvalue\n\"master_key\": \"sk-local-deadbeefdeadbeef\" x-api-key=QQQQQQQQ");
        Assert.DoesNotContain("abc123secretvalue", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeefdeadbeef", text, StringComparison.Ordinal);
        Assert.DoesNotContain("QQQQQQQQ", text, StringComparison.Ordinal);
        Assert.Equal("nothing secret here https://github.com/acme/shop", Redactor.Redact("nothing secret here https://github.com/acme/shop"));
    }

    [Fact]
    public void RegisteredKeysAreReplacedEverywhere()
    {
        Redactor.Register("sk-test-SECRET-123");
        Assert.Equal("key=*** and again ***", Redactor.Redact("key=sk-test-SECRET-123 and again sk-test-SECRET-123"));
    }

    [Theory]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "Authorization: ***")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", "Authorization: ***")]
    [InlineData("{\"Authorization\": \"Bearer abcdefghijklmnop\", \"x\": 1}", "{\"Authorization\": \"***\", \"x\": 1}")]
    [InlineData("curl -H \"x-api-key: 1234567890abcdef\" https://api", "curl -H \"x-api-key: ***\" https://api")]
    [InlineData("token Bearer abcdefghijklmnop end", "token Bearer *** end")]
    public void HeaderValuesAreMasked(string input, string expected)
    {
        Assert.Equal(expected, Redactor.Redact(input));
    }

    [Fact]
    public void ShortStringsAreNotRegistered()
    {
        Redactor.Register("abc");
        Assert.Equal("abc", Redactor.Redact("abc"));
    }
}
