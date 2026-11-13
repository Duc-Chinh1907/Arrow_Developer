namespace Arrow.Business.Analysis;

/// <summary>Order in which JDKs are tried, the first being the choice, and the reason written to M.</summary>
/// <param name="Order">JDK majors, first choice first.</param>
/// <param name="Reason"><c>declared</c>, <c>default</c>, or <c>none</c> when no JDK is available.</param>
/// <param name="BelowDeclared">No available JDK reaches the declared level; the largest is used and a warning is shown.</param>
public sealed record JdkPlan(IReadOnlyList<int> Order, string Reason, bool BelowDeclared);

/// <summary>Chooses the JDK of a module (P01 §4.3 step 2–3).</summary>
public static class JdkSelector
{
    /// <summary>
    /// With a declared level: the smallest JDK whose major is at least that level (a level below 8 counts as 8); without one, JDK 17,
    /// or the largest JDK when 17 is missing. When a JDK fails to compile, the others are tried in ascending order.
    /// </summary>
    public static JdkPlan Select(int? declaredMajor, IReadOnlyCollection<int> available)
    {
        var sorted = available.Distinct().Order().ToList();
        if (sorted.Count == 0)
        {
            return new JdkPlan([], "none", false);
        }

        int first;
        string reason;
        var below = false;
        if (declaredMajor is { } declared)
        {
            var wanted = Math.Max(declared, 8);
            below = sorted[^1] < wanted;
            first = below ? sorted[^1] : sorted.First(m => m >= wanted);
            reason = "declared";
        }
        else
        {
            first = sorted.Contains(17) ? 17 : sorted[^1];
            reason = "default";
        }

        return new JdkPlan([first, .. sorted.Where(m => m != first)], reason, below);
    }

    /// <summary>Reason written when the JDK used is not the first choice: <c>fallback:17,8</c> (the JDKs tried, in order).</summary>
    public static string FallbackReason(IEnumerable<int> tried) => "fallback:" + string.Join(',', tried);

    /// <summary>Signs in a test run log that the test JVM could not start with this JDK (P01 §4.3 step 3).</summary>
    public static IReadOnlyList<string> JvmStartFailureMarkers { get; } =
    [
        "stack size specified is too small",
        "Could not create the Java Virtual Machine",
        "Error occurred during initialization of VM",
        "Unrecognized VM option",
        "The forked VM terminated without properly saying goodbye",
    ];

    /// <summary>The first baseline run reported no test and the log shows that the test JVM did not start.</summary>
    public static bool IsJvmStartFailure(string output, int testsReported) =>
        testsReported == 0 && JvmStartFailureMarkers.Any(m => output.Contains(m, StringComparison.Ordinal));
}
