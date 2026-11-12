namespace Arrow.Common;

/// <summary>
/// The class under test once block 1 has located it (P01 §3.3, §5.1, §8).
/// Paths are relative to the project root and use <c>/</c> (P00 §5).
/// </summary>
/// <param name="Fqn">Fully qualified name, e.g. <c>com.myshop.service.OrderService</c>.</param>
/// <param name="SimpleName">Simple name, e.g. <c>OrderService</c>.</param>
/// <param name="Package">Package name; empty for the default package.</param>
/// <param name="Kind">Kind of the top-level type.</param>
/// <param name="CutPath">Source file of the CUT.</param>
/// <param name="SourceSha256">sha256 of the CUT source, used to detect changes before writing (P06).</param>
/// <param name="GeneratedTestPath">Where the generated test goes, e.g. <c>…/OrderService_ArrowTest.java</c> (P01 §4.7).</param>
/// <param name="GeneratedTestFqn">FQN of the generated test class.</param>
/// <param name="NameIndex">k of <c>_Arrow&lt;k&gt;Test</c>; 1 for <c>_ArrowTest</c>.</param>
public sealed record CutInfo(
    string Fqn,
    string SimpleName,
    string Package,
    CutKind Kind,
    string CutPath,
    string SourceSha256,
    string GeneratedTestPath,
    string GeneratedTestFqn,
    int NameIndex);
