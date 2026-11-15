using Arrow.Business.Analysis;

namespace Arrow.Business.Tests.Analysis;

// P01 §6.2 (16/11/2026): a cached baseline is reused only when everything that decides its outcome is unchanged.
public sealed class BaselineCacheKeyTests
{
    private static readonly BaselineCacheInputs Inputs = new(
        "sha256:abc", ".", "Standalone", "-pl core -am", "Maven /opt/mvn 3.9.16", "17 /opt/jdk-17",
        ["mvn", "test"], ["core/target/classes", "/m2/junit-4.13.2.jar", "/m2/lib-1.0-SNAPSHOT.jar"], 2);

    private static (long, DateTime)? Stamp(string path) => (100, new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void TheSameInputsGiveTheSameKey()
    {
        var key = BaselineCacheKey.Compute(Inputs, Stamp);
        Assert.Equal(key, BaselineCacheKey.Compute(Inputs with { }, Stamp));
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void AnyChangedInputGivesAnotherKey()
    {
        var key = BaselineCacheKey.Compute(Inputs, Stamp);
        BaselineCacheInputs[] changed =
        [
            Inputs with { Code = "sha256:def" },
            Inputs with { BuildRoot = "core" },
            Inputs with { Mode = "Reactor" },
            Inputs with { Selector = "-pl api -am" },
            Inputs with { Tool = "Maven /opt/mvn 3.9.9" },
            Inputs with { Jdk = "21 /opt/jdk-21" },
            Inputs with { ModuleTest = ["mvn", "-o", "test"] },
            Inputs with { Classpath = ["core/target/classes", "/m2/junit-4.13.2.jar"] },
            Inputs with { Runs = 3 },
        ];
        Assert.All(changed, c => Assert.NotEqual(key, BaselineCacheKey.Compute(c, Stamp)));
    }

    // A SNAPSHOT jar replaced at the same path changes the key through its size and time.
    [Fact]
    public void AChangedJarGivesAnotherKey()
    {
        var key = BaselineCacheKey.Compute(Inputs, Stamp);
        var rebuilt = BaselineCacheKey.Compute(Inputs, p => p.EndsWith("SNAPSHOT.jar", StringComparison.Ordinal)
            ? (100, new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc))
            : Stamp(p));
        Assert.NotEqual(key, rebuilt);
    }

    [Fact]
    public void ACachedBaselineIsUsedForSevenDays()
    {
        var measured = new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero);
        Assert.True(BaselineCacheKey.Fresh(measured, measured.AddDays(7)));
        Assert.False(BaselineCacheKey.Fresh(measured, measured.AddDays(7).AddMinutes(1)));
        Assert.False(BaselineCacheKey.Fresh(measured.AddDays(1), measured)); // from the future: the clock changed
    }
}
