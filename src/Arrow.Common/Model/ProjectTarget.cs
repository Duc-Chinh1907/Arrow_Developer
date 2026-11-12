namespace Arrow.Common;

/// <summary>What the user asked to test (P01 §2.1).</summary>
/// <param name="Kind">Local folder or GitHub repository.</param>
/// <param name="Location">Absolute folder path, or the https URL of the repository.</param>
/// <param name="GitRef">Branch, tag or SHA; only for <see cref="ProjectSourceKind.GitRepository"/>. Null means the default branch.</param>
/// <param name="CutQuery">Simple name, FQN, or path of a <c>.java</c> file (relative to the project root or absolute).</param>
public sealed record ProjectTarget(
    ProjectSourceKind Kind,
    string Location,
    string? GitRef,
    string CutQuery);
