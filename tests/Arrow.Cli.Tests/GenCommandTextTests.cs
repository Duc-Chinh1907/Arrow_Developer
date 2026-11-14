using Arrow.Cli.Commands;
using Arrow.Common;

namespace Arrow.Cli.Tests;

public sealed class GenCommandTextTests
{
    // GD7 pilot: the summary table printed the enum names ("remoteproject", "notvalid").
    [Fact]
    public void EveryApplyDecisionHasAVietnameseLabel()
    {
        Assert.Equal("chỉ trong runs/", GenCommand.DecisionText(ApplyDecision.RemoteProject));
        Assert.Equal("đã ghi", GenCommand.DecisionText(ApplyDecision.Written));
        Assert.All(Enum.GetValues<ApplyDecision>(), d => Assert.NotEqual(d.ToString(), GenCommand.DecisionText(d)));
    }
}
