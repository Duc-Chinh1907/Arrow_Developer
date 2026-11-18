using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Generation;
using Arrow.Common.Json;

namespace Arrow.Business.Tests.UseCases;

// prompts.strategy (Chính, 18/11/2026): the three strategies of R02 §3 for c0, the default being zero_shot_project_aware.
public sealed class PromptStrategyTests
{
    [Fact]
    public void OnlyTheOtherStrategiesAreWarnedAbout()
    {
        Assert.Null(GenerateTestsUseCase.StrategyWarning(GenerationStrategies.ZeroShotProjectAware));
        Assert.Contains("zero_shot", GenerateTestsUseCase.StrategyWarning(GenerationStrategies.ZeroShot), StringComparison.Ordinal);
        Assert.Contains("few_shot", GenerateTestsUseCase.StrategyWarning(GenerationStrategies.FewShot), StringComparison.Ordinal);
    }

    [Fact]
    public void EachStrategyHasItsTemplates()
    {
        Assert.Equal(("generation/system/zero_shot_project_aware.md", "generation/user/project_aware.md"),
            (GenerationStrategies.SystemPath(GenerationStrategies.ZeroShotProjectAware), GenerationStrategies.UserPath(GenerationStrategies.ZeroShotProjectAware)));
        Assert.Equal(("generation/system/zero_shot.md", "generation/user/default.md"),
            (GenerationStrategies.SystemPath(GenerationStrategies.ZeroShot), GenerationStrategies.UserPath(GenerationStrategies.ZeroShot)));
        Assert.Equal(("generation/system/few_shot.md", "generation/user/default.md"),
            (GenerationStrategies.SystemPath(GenerationStrategies.FewShot), GenerationStrategies.UserPath(GenerationStrategies.FewShot)));
        Assert.False(GenerationStrategies.IsKnown("ZERO_SHOT"));
    }

    // A run.json written before the setting has no strategy: it is read as the default, which is how that run ran.
    [Fact]
    public void ARunJsonWithoutStrategyUsedTheDefault()
    {
        var old = ArrowJson.Deserialize<RunConfig>("""{ "model": "gpt-x", "provider_host": "h", "rounds": 5, "early_stop_p": 3, "repair": true, "quality": true }""");
        Assert.Equal(GenerationStrategies.ZeroShotProjectAware, old.Strategy);

        var json = ArrowJson.Serialize(new RunConfig("gpt-x", "h", 5, 3, true, true, Strategy: GenerationStrategies.FewShot), indented: false);
        Assert.Contains("\"strategy\":\"few_shot\"", json, StringComparison.Ordinal);
        Assert.Equal(GenerationStrategies.FewShot, ArrowJson.Deserialize<RunConfig>(json).Strategy);
    }
}
