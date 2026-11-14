using Arrow.Business.Analysis;

namespace Arrow.Business.Tests.Analysis;

public sealed class TargetDirTests
{
    [Fact]
    public void AFqnIsKeptAsIs() => Assert.Equal("targets/com.acme.Order", ProjectAnalyzer.TargetDir("com.acme.Order"));

    // GD7: a class name with a pasted \r ended the run with CLI_INTERNAL when the folder was created.
    [Theory]
    [InlineData("Order" + "\r", "targets/Order_")]
    [InlineData("No:Such*Class", "targets/No_Such_Class")]
    [InlineData("a/b" + "\\" + "c", "targets/a_b_c")]
    public void CharactersAFolderCannotHaveBecomeUnderscores(string query, string expected) =>
        Assert.Equal(expected, ProjectAnalyzer.TargetDir(query));
}
