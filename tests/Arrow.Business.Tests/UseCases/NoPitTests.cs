using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Json;

namespace Arrow.Business.Tests.UseCases;

// --no-pit (Chính, 15/11/2026): block 4 measures JaCoCo and tsDetect only.
public sealed class NoPitTests
{
    private static readonly string[] Configured = ["jacoco", "pit", "tsdetect"];

    [Fact]
    public void NoPitDropsOnlyPit()
    {
        Assert.Equal(["jacoco", "tsdetect"], GenerateTestsUseCase.MeasuredTools(Configured, noPit: true));
        Assert.Equal(Configured, GenerateTestsUseCase.MeasuredTools(Configured, noPit: false));
        Assert.Equal(["jacoco"], GenerateTestsUseCase.MeasuredTools(["jacoco", "PIT"], noPit: true));
    }

    // A run.json written before the flag has no no_pit: it is read as "PIT measured", which is how that run ran.
    [Fact]
    public void ARunJsonWithoutNoPitMeansPitWasMeasured()
    {
        var old = ArrowJson.Deserialize<RunConfig>("""{ "model": "gpt-x", "provider_host": "h", "rounds": 5, "early_stop_p": 3, "repair": true, "quality": true }""");
        Assert.False(old.NoPit);

        var json = ArrowJson.Serialize(new RunConfig("gpt-x", "h", 5, 3, true, true, NoPit: true), indented: false);
        Assert.Contains("\"no_pit\":true", json, StringComparison.Ordinal);
        Assert.True(ArrowJson.Deserialize<RunConfig>(json).NoPit);
    }
}
