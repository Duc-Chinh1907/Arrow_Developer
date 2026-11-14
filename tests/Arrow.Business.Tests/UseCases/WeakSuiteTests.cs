using Arrow.Business.UseCases;
using Arrow.Common;

namespace Arrow.Business.Tests.UseCases;

// Chính's decision (GD7): a VALID suite with line < 30% or mutation 0% gets a warning before the write question.
public sealed class WeakSuiteTests
{
    private static TargetMetrics M(double? line, double? mutation) => new(line, null, null, mutation, null, null);

    [Theory]
    [InlineData(0.22, 0.0, "line 22%, mutation 0%")] // jbock Optionalish
    [InlineData(0.06, null, "line 6%")]               // PIT not measured: only line counts
    [InlineData(0.75, 0.0, "mutation 0%")]
    [InlineData(0.299, 0.5, "line 30%")]
    public void WeakSuitesSayWhy(double? line, double? mutation, string expected) =>
        Assert.Equal(expected, TargetPipeline.WeakSuite(M(line, mutation)));

    [Theory]
    [InlineData(0.30, 0.01)]
    [InlineData(0.97, 0.87)]
    [InlineData(null, null)]
    public void OtherSuitesAreNotWeak(double? line, double? mutation) =>
        Assert.Null(TargetPipeline.WeakSuite(M(line, mutation)));

    [Fact]
    public void NoMeasurementIsNotWeak() => Assert.Null(TargetPipeline.WeakSuite(null));
}
