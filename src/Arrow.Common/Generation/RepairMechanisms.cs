namespace Arrow.Common.Generation;

/// <summary>
/// The repair mechanisms of <c>repair.mechanism</c> (P04 §6.7, Chính 20/11/2026). <c>adaptive</c> is the product; the three
/// others are the comparison branches of the team's experiment TN2 (F+, F−, F2).
/// </summary>
public static class RepairMechanisms
{
    /// <summary>A: Adaptive Repair, 3 prompt levels with checkpoint, rollback, signature and code hash (P04 §6.3).</summary>
    public const string Adaptive = "adaptive";

    /// <summary>F+: Fixed Repair (R03 §6.6), the level 1 prompt every round, always from the latest version.</summary>
    public const string FixedPlus = "fixed_plus";

    /// <summary>F−: as F+, but the prompt has no repository configuration block; verification still uses the configuration.</summary>
    public const string FixedMinus = "fixed_minus";

    /// <summary>F2: as F+, but the level 2 prompt every round (<c>extra_context</c> and <c>failed_attempts</c>).</summary>
    public const string FixedLevel2 = "fixed_l2";

    /// <summary>The default.</summary>
    public const string Default = Adaptive;

    /// <summary>Every mechanism, in the order of the documentation.</summary>
    public static IReadOnlyList<string> All { get; } = [Adaptive, FixedPlus, FixedMinus, FixedLevel2];

    /// <summary>Whether <paramref name="mechanism"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? mechanism) => mechanism is not null && All.Contains(mechanism, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="mechanism"/> is one of the fixed branches (no checkpoint, no rollback, no level change).</summary>
    public static bool IsFixed(string mechanism) => mechanism is FixedPlus or FixedMinus or FixedLevel2;
}
