using Arrow.Cli.Rendering;
using Arrow.Common.Config;
using Arrow.Common.Errors;

namespace Arrow.Cli.Tests;

/// <summary>Output mode (P08 §3.1) and exit codes (P08 §8).</summary>
public class RenderModeTests
{
    [Theory]
    [InlineData(false, false, false, null, ColorMode.Auto, RenderMode.Rich)]
    [InlineData(false, false, true, null, ColorMode.Auto, RenderMode.Plain)]
    [InlineData(true, false, false, null, ColorMode.Auto, RenderMode.Plain)]
    [InlineData(false, false, false, "1", ColorMode.Auto, RenderMode.Plain)]
    [InlineData(false, false, false, null, ColorMode.Never, RenderMode.Plain)]
    [InlineData(false, false, true, null, ColorMode.Always, RenderMode.Rich)]
    [InlineData(true, true, false, null, ColorMode.Always, RenderMode.Json)]
    [InlineData(false, true, true, "1", ColorMode.Auto, RenderMode.Json)]
    public void ModeFollowsP08(bool plain, bool json, bool redirected, string? noColor, ColorMode color, RenderMode expected)
    {
        Assert.Equal(expected, RenderModes.Select(plain, json, redirected, noColor, color));
    }

    [Theory]
    [InlineData(ErrorCodes.CliArgs, ExitCodes.Usage)]
    [InlineData(ErrorCodes.CfgInvalid, ExitCodes.Usage)]
    [InlineData(ErrorCodes.B1JdkMissing, ExitCodes.Usage)]
    [InlineData(ErrorCodes.CliInternal, ExitCodes.Internal)]
    public void ExitCodeOfAnError(string code, int expected)
    {
        Assert.Equal(expected, ExitCodes.For(new ArrowException(code)));
    }

    [Fact]
    public void PlainTablesAlignColumnsAndJsonModeKeepsStdoutClean()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var plain = new Renderer(RenderMode.Plain, stdout, stderr);
        plain.Table(["Tên", "Khoá"], [["teamo", "...-123"], ["a", "b"]]);
        Assert.Equal("Tên    Khoá\n-----  -------\nteamo  ...-123\na      b\n", stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));

        using var jsonOut = new StringWriter();
        using var jsonErr = new StringWriter();
        var json = new Renderer(RenderMode.Json, jsonOut, jsonErr);
        json.Ok("xong");
        json.Table(["a"], [["b"]]);
        json.Json(new { done = true });
        Assert.Equal("{\n  \"done\": true\n}\n", jsonOut.ToString());
        Assert.Contains("[OK] xong", jsonErr.ToString(), StringComparison.Ordinal);
    }
}
