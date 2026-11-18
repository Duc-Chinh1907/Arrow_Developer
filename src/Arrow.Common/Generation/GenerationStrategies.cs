namespace Arrow.Common.Generation;

/// <summary>
/// The prompting strategies of the first generation, c0 (R02 §3), chosen with <c>prompts.strategy</c> (P02 §3.1). Only the GENERATE
/// prompt depends on it: every repair prompt has the repository configuration, whatever the strategy.
/// </summary>
public static class GenerationStrategies
{
    /// <summary>System and user prompt with the repository configuration (block M); the default.</summary>
    public const string ZeroShotProjectAware = "zero_shot_project_aware";

    /// <summary>No repository configuration: the model is not told the test framework of the project.</summary>
    public const string ZeroShot = "zero_shot";

    /// <summary>Like <see cref="ZeroShot"/>, with two fixed human-written examples (JUnit 4 and JUnit 5) in the system prompt.</summary>
    public const string FewShot = "few_shot";

    /// <summary>The strategy when none is configured.</summary>
    public const string Default = ZeroShotProjectAware;

    /// <summary>Every strategy, the default first.</summary>
    public static IReadOnlyList<string> All { get; } = [ZeroShotProjectAware, ZeroShot, FewShot];

    /// <summary>True for a name of <see cref="All"/>.</summary>
    public static bool IsKnown(string? strategy) => strategy is not null && All.Contains(strategy);

    /// <summary>Path of the system template of <paramref name="strategy"/> below <c>Prompts/</c>.</summary>
    public static string SystemPath(string strategy) => $"generation/system/{Checked(strategy)}.md";

    /// <summary>Path of the user template: <c>project_aware.md</c> with block M, <c>default.md</c> without it (R02 §3).</summary>
    public static string UserPath(string strategy) =>
        Checked(strategy) == ZeroShotProjectAware ? "generation/user/project_aware.md" : "generation/user/default.md";

    private static string Checked(string strategy) =>
        IsKnown(strategy) ? strategy : throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Quy trình sinh test không hợp lệ.");
}
