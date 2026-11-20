using Arrow.Business.Repair;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Config;
using Arrow.Common.Generation;
using Arrow.Common.Json;
using Arrow.Common.Verification;

namespace Arrow.Business.Tests.UseCases;

// repair.mechanism (Chính, 20/11/2026): adaptive, and the TN2 branches F+, F− and F2.
public sealed class RepairMechanismTests
{
    [Fact]
    public void TheDefaultIsAdaptive()
    {
        Assert.Equal(RepairMechanisms.Adaptive, ArrowDefaults.Config.Repair.Mechanism);
        Assert.Equal(["adaptive", "fixed_plus", "fixed_minus", "fixed_l2"], RepairMechanisms.All);
        Assert.False(RepairMechanisms.IsKnown("FIXED_PLUS"));
        Assert.False(RepairMechanisms.IsFixed(RepairMechanisms.Adaptive));
    }

    [Fact]
    public void OnlyTheFixedMechanismsAreWarnedAbout()
    {
        Assert.Null(GenerateTestsUseCase.MechanismWarning(RepairMechanisms.Adaptive));
        foreach (var mechanism in new[] { RepairMechanisms.FixedPlus, RepairMechanisms.FixedMinus, RepairMechanisms.FixedLevel2 })
        {
            Assert.Contains(mechanism, GenerateTestsUseCase.MechanismWarning(mechanism), StringComparison.Ordinal);
        }
    }

    // A run.json written before the setting has no mechanism: it ran with Adaptive.
    [Fact]
    public void ARunJsonWithoutMechanismUsedAdaptive()
    {
        var old = ArrowJson.Deserialize<RunConfig>("""{ "model": "gpt-x", "provider_host": "h", "rounds": 5, "early_stop_p": 3, "repair": true, "quality": true }""");
        Assert.Equal(RepairMechanisms.Adaptive, old.Mechanism);

        var json = ArrowJson.Serialize(new RunConfig("gpt-x", "h", 5, 3, true, true, Mechanism: RepairMechanisms.FixedMinus), indented: false);
        Assert.Contains("\"mechanism\":\"fixed_minus\"", json, StringComparison.Ordinal);
        Assert.Equal(RepairMechanisms.FixedMinus, ArrowJson.Deserialize<RunConfig>(json).Mechanism);
    }

    [Fact]
    public void TheRoundLineOfAFixedMechanismSaysItRepairsTheLatestVersion()
    {
        var round = new RoundDecision { Round = 2, Mechanism = RepairMechanisms.FixedPlus, RequestedLevel = 1, Level = 1, LevelAfter = 1, State = VerificationState.Compile };
        Assert.Equal("vòng 2 · mức 1 → COMPILE · sửa tiếp từ bản này", RepairRunner.RoundText(round));
        Assert.Equal("vòng 2 · mức 1 → output không dùng được · giữ bản trước", RepairRunner.RoundText(round with { State = null, InvalidOutput = true }));
    }
}
