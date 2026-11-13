namespace Arrow.Common.Quality;

/// <summary><c>block4/jacoco.json</c> (P05 §4).</summary>
/// <param name="Line">Line coverage of the CUT and its nested classes; null when it has no line.</param>
/// <param name="Branch">Branch coverage; null when the CUT has no branch.</param>
/// <param name="Method">Method coverage; null when it has no method.</param>
/// <param name="Counters"><c>LINE_COVERED</c>, <c>LINE_MISSED</c>, <c>BRANCH_*</c>, <c>METHOD_*</c>.</param>
/// <param name="Tool">Tool and version, e.g. <c>jacoco 0.8.12</c>.</param>
/// <param name="Seconds">Time of the measurement.</param>
public sealed record CoverageResult(double? Line, double? Branch, double? Method, IReadOnlyDictionary<string, int> Counters, string Tool = "", double Seconds = 0);

/// <summary>A surviving mutant, to show where the test is weak (P05 §5).</summary>
/// <param name="Method">Mutated method.</param>
/// <param name="Line">Line of the mutation.</param>
/// <param name="Mutator">Short name of the mutator, e.g. <c>ConditionalsBoundaryMutator</c>.</param>
public sealed record SurvivorSample(string Method, int? Line, string Mutator);

/// <summary><c>block4/pit.json</c> (P05 §5).</summary>
/// <param name="Total">Mutants.</param>
/// <param name="Killed">KILLED.</param>
/// <param name="Survived">SURVIVED.</param>
/// <param name="NoCoverage">NO_COVERAGE.</param>
/// <param name="TimedOut">TIMED_OUT.</param>
/// <param name="MemoryError">MEMORY_ERROR.</param>
/// <param name="RunError">RUN_ERROR.</param>
/// <param name="Score">(KILLED + TIMED_OUT + MEMORY_ERROR) / total; null without mutants.</param>
/// <param name="TestStrength">KILLED / (total − NO_COVERAGE), R04 §4.3; null when the denominator is 0.</param>
/// <param name="SurvivorsSample">The first 10 surviving mutants.</param>
/// <param name="Tool">Tool and version.</param>
/// <param name="Seconds">Time of the measurement.</param>
public sealed record MutationResult(
    int Total,
    int Killed,
    int Survived,
    int NoCoverage,
    int TimedOut,
    int MemoryError,
    int RunError,
    double? Score,
    double? TestStrength,
    IReadOnlyList<SurvivorSample> SurvivorsSample,
    string Tool = "",
    double Seconds = 0);

/// <summary><c>block4/tsdetect.json</c> (P05 §6).</summary>
/// <param name="TestMethods">Test methods, counted by java-helper.</param>
/// <param name="Smells">Count of each smell found (only those found).</param>
/// <param name="TotalSmells">Sum of the counts.</param>
/// <param name="Density">Smells per test method; null without test methods.</param>
/// <param name="Tool">Tool and version.</param>
/// <param name="Seconds">Time of the measurement.</param>
public sealed record SmellResult(int TestMethods, IReadOnlyDictionary<string, int> Smells, int TotalSmells, double? Density, string Tool = "", double Seconds = 0);

/// <summary>State of one tool in <c>block4/outcome.json</c> (P05 §7).</summary>
/// <param name="Status">OK, FAILED or SKIPPED.</param>
/// <param name="Code">Error code when not OK.</param>
/// <param name="Message">Error message (for PIT, the last 20 lines of the log).</param>
/// <param name="Attempts">Attempts made.</param>
/// <param name="Seconds">Time of all attempts.</param>
public sealed record ToolOutcome(ToolStatus Status, string? Code = null, string? Message = null, int Attempts = 1, double Seconds = 0);

/// <summary>What block 4 measured for one CUT; copied into <c>result.json</c> (P05 §7).</summary>
/// <param name="Coverage">JaCoCo; null when it failed or was off.</param>
/// <param name="Mutation">PIT; null when it failed or was off.</param>
/// <param name="Smells">tsDetect; null when it failed or was off.</param>
/// <param name="Tools">State of each tool that was asked for.</param>
public sealed record QualityResult(CoverageResult? Coverage, MutationResult? Mutation, SmellResult? Smells, IReadOnlyDictionary<string, ToolOutcome> Tools)
{
    /// <summary>The metrics of <c>result.json</c>.</summary>
    public TargetMetrics Metrics => new(Coverage?.Line, Coverage?.Branch, Coverage?.Method, Mutation?.Score, Mutation?.TestStrength, Smells?.Density);
}
