using System.Text.RegularExpressions;
using Arrow.Common.Errors;
using Arrow.Common.Json;

namespace Arrow.Business.Tests.Common;

/// <summary>Error codes of P00 §6 and the error tables of P01–P10.</summary>
public class ErrorCatalogTests
{
    public static TheoryData<string> Languages => new(ErrorCatalog.Languages);

    [Fact]
    public void CodesFollowThePrefixesOfP00()
    {
        Assert.Equal(57, ErrorCodes.All.Count);
        Assert.Equal(ErrorCodes.All.Count, ErrorCodes.All.Distinct().Count());
        Assert.All(ErrorCodes.All, code => Assert.Matches(@"^(B1|B2|B3|B4|W|CFG|JH|GEN|CLI)_[A-Z0-9_]+$", code));
    }

    [Fact]
    public void ResearchOnlyCodesAreAbsent()
    {
        // P00 §7: these belong to the team's experiments, not to the product.
        string[] research = ["B1_MANIFEST_INVALID", "B1_PAIRED_TEST_NOT_PASSING", "B2_CALL_LIMIT", "B4_REPORT_MISMATCH"];
        Assert.Empty(ErrorCodes.All.Intersect(research));
        Assert.DoesNotContain(ErrorCodes.All, code => code.StartsWith("B4_EVOSUITE", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryCodeHasAMessageAndAHint(string language)
    {
        var catalog = ErrorCatalog.For(language);
        Assert.Equal(language, catalog.Language);
        Assert.Equal(ErrorCodes.All.Order(), catalog.Codes.Order());
        foreach (var code in ErrorCodes.All)
        {
            var entry = catalog.Find(code)!;
            Assert.False(string.IsNullOrWhiteSpace(entry.Message), $"{language} {code}: empty message");
            if (code != ErrorCodes.JhInputTooLarge)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Hint), $"{language} {code}: empty hint");
            }
        }
    }

    [Fact]
    public void LanguagesUseTheSamePlaceholders()
    {
        var vi = ErrorCatalog.For("vi");
        var en = ErrorCatalog.For("en");
        foreach (var code in ErrorCodes.All)
        {
            Assert.True(ErrorCatalog.Placeholders(vi.Find(code)!.Message).SetEquals(ErrorCatalog.Placeholders(en.Find(code)!.Message)), $"{code}: message placeholders differ");
            Assert.True(ErrorCatalog.Placeholders(vi.Find(code)!.Hint).SetEquals(ErrorCatalog.Placeholders(en.Find(code)!.Hint)), $"{code}: hint placeholders differ");
        }
    }

    [Fact]
    public void VietnameseCatalogIsWrittenInVietnamese()
    {
        var vi = ErrorCatalog.For("vi");
        Assert.Equal("Máy không có JDK nào dùng được.", vi.Find(ErrorCodes.B1JdkMissing)!.Message);
        Assert.Matches(new Regex("[ăâđêôơưàáạảãèéẹẻẽìíịỉĩòóọỏõùúụủũỳýỵỷỹ]"), string.Concat(ErrorCodes.All.Select(c => vi.Find(c)!.Message)));
    }

    [Fact]
    public void DescribeFillsPlaceholders()
    {
        var info = ErrorCatalog.For("vi").Describe(ErrorCodes.B1JdkMissing, args: new Dictionary<string, string> { ["jdk"] = "17" }, log: "block1/logs/compile.log");
        Assert.Equal(new ErrorInfo("B1_JDK_MISSING", "Máy không có JDK nào dùng được.", "Cài JDK 17, rồi chạy \"arrow setup\".", "block1/logs/compile.log"), info);
    }

    [Fact]
    public void MissingArgumentsStayVisible()
    {
        var info = ErrorCatalog.For("en").Describe(ErrorCodes.B2WrongModel, args: new Dictionary<string, string> { ["actual"] = "gpt-4o" });
        Assert.Equal("The endpoint answered with another model: gpt-4o instead of {expected}.", info.Message);
    }

    [Fact]
    public void DetailReplacesTheCatalogMessage()
    {
        var exception = new ArrowException(ErrorCodes.B1BaselineBuildFailed, "Không biên dịch được module shop-core với JDK 17, 8, 11, 21");
        var info = ErrorCatalog.For("vi").Describe(exception);
        Assert.Equal("Không biên dịch được module shop-core với JDK 17, 8, 11, 21", info.Message);
        Assert.StartsWith("Xem lỗi đầu tiên", info.Hint, StringComparison.Ordinal);
        Assert.Equal("B1_BASELINE_BUILD_FAILED: Không biên dịch được module shop-core với JDK 17, 8, 11, 21", exception.Message);
    }

    [Fact]
    public void UnknownCodeKeepsTheCode()
    {
        Assert.Equal(new ErrorInfo("X_UNKNOWN", "X_UNKNOWN", null, null), ErrorCatalog.For("vi").Describe("X_UNKNOWN"));
    }

    [Theory]
    [InlineData("EN", "en")]
    [InlineData("fr", "vi")]
    [InlineData(null, "vi")]
    public void LanguageFallsBackToVietnamese(string? requested, string expected)
    {
        Assert.Equal(expected, ErrorCatalog.For(requested).Language);
    }

    // GD7: "arrow gen --resume {run_id}" and "file log: {log_path}" were printed with the raw placeholder.
    [Fact]
    public void ContextValuesFillHintsThatTheCallerLeftOpen()
    {
        var catalog = ErrorCatalog.For("vi").With("run_id", "20261114-1200-abcd");

        var budget = catalog.Describe(ErrorCodes.B2TokenBudget, "Đã dùng 10 token.");
        Assert.Equal("Đã dùng 10 token.", budget.Message);
        Assert.Contains("arrow gen --resume 20261114-1200-abcd", budget.Hint, StringComparison.Ordinal);

        var own = catalog.Describe(ErrorCodes.B2TokenBudget, args: new Dictionary<string, string> { ["run_id"] = "other" });
        Assert.Contains("--resume other", own.Hint, StringComparison.Ordinal);

        var internalError = ErrorCatalog.For("vi").Describe(ErrorCodes.CliInternal, "boom", log: "logs/x.log");
        Assert.Contains("logs/x.log", internalError.Hint, StringComparison.Ordinal);

        // The shared catalog is unchanged, and a value nobody gave stays visible.
        Assert.Contains("{run_id}", ErrorCatalog.For("vi").Describe(ErrorCodes.B2TokenBudget).Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorInfoUsesTheResultJsonShape()
    {
        var json = ArrowJson.Serialize(new ErrorInfo("W_IO", "m", null, "logs/x.log"), indented: false);
        Assert.Equal("{\"code\":\"W_IO\",\"message\":\"m\",\"hint\":null,\"log\":\"logs/x.log\"}", json);
    }
}
