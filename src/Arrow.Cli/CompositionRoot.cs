namespace Arrow.Cli;

/// <summary>
/// Registers the DataAccess and Business implementations for dependency injection (P08 §9).
/// It is the only Cli type allowed to use <c>Arrow.DataAccess</c>; <c>ArchitectureTests</c> checks this.
/// </summary>
internal static class CompositionRoot
{
}
