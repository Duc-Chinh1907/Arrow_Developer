using Arrow.Business.Analysis;
using Arrow.Common;
using Arrow.Common.Errors;

namespace Arrow.Business.Tests.Analysis;

/// <summary>Finding the CUT (P01 §3.3, §11).</summary>
public class ClassLocatorTests
{
    private static ClassCandidate C(string fqn, CutKind kind = CutKind.Class, bool isAbstract = false, string? path = null)
    {
        var simple = fqn[(fqn.LastIndexOf('.') + 1)..];
        return new ClassCandidate(fqn, simple, kind, isAbstract, path ?? "src/main/java/" + fqn.Replace('.', '/') + ".java");
    }

    private static readonly IReadOnlyList<ClassCandidate> Classes =
    [
        C("com.shop.OrderService"),
        C("com.shop.Order"),
        C("com.shop.billing.Invoice"),
        C("com.legacy.Invoice"),
        C("com.shop.Repo", CutKind.Interface),
        C("com.shop.Audited", CutKind.Annotation),
        C("com.shop.Base", isAbstract: true),
        C("com.shop.Status", CutKind.Enum),
        C("com.shop.Money", CutKind.Record),
        C("com.shop.Helper", path: "src/main/java/com/shop/Order.java"),
    ];

    [Fact]
    public void MatchesSimpleNameFqnAndPath()
    {
        Assert.Equal("com.shop.OrderService", Found(ClassLocator.Locate(Classes, "OrderService")).Fqn);
        Assert.Equal("com.shop.billing.Invoice", Found(ClassLocator.Locate(Classes, "com.shop.billing.Invoice")).Fqn);
        Assert.Equal("com.shop.Order", Found(ClassLocator.Locate(Classes, "src/main/java/com/shop/Order.java")).Fqn);
        Assert.Equal("com.shop.Order", Found(ClassLocator.Locate(Classes, @"src\main\java\com\shop\Order.java")).Fqn);
        Assert.Equal("com.shop.Order", Found(ClassLocator.Locate(Classes, "Order.java")).Fqn);
    }

    [Fact]
    public void CaseInsensitiveOnlyWithoutAnExactMatch()
    {
        Assert.Equal("com.shop.OrderService", Found(ClassLocator.Locate(Classes, "orderservice")).Fqn);
        var withBoth = new List<ClassCandidate>(Classes) { C("com.x.orderservice") };
        Assert.Equal("com.x.orderservice", Found(ClassLocator.Locate(withBoth, "orderservice")).Fqn);
    }

    [Fact]
    public void SameSimpleNameIsAmbiguous()
    {
        var ambiguous = Assert.IsType<LocateResult.Ambiguous>(ClassLocator.Locate(Classes, "Invoice"));
        Assert.Equal(["com.legacy.Invoice", "com.shop.billing.Invoice"], ambiguous.Candidates.Select(c => c.Fqn));
    }

    [Fact]
    public void MissingClassSuggestsNearNames()
    {
        var failed = Assert.IsType<LocateResult.Failed>(ClassLocator.Locate(Classes, "OrderServis"));
        Assert.Equal(ErrorCodes.B1CutMissing, failed.Code);
        Assert.Equal(["com.shop.OrderService"], failed.Suggestions);

        // "contains the typed text" also counts; at most 5 suggestions.
        Assert.Equal(["com.shop.Order", "com.shop.OrderService"], ClassLocator.Suggest(Classes, "order"));
        var many = Enumerable.Range(0, 9).Select(i => C($"p.Item{i}")).ToList();
        Assert.Equal(5, ClassLocator.Suggest(many, "Item").Count);
        Assert.Equal(3, ClassLocator.Levenshtein("kitten", "sitting"));
    }

    [Fact]
    public void KindsThatCannotBeTestedAreRejected()
    {
        Assert.Equal(ErrorCodes.B1CutNotTestable, Assert.IsType<LocateResult.Failed>(ClassLocator.Locate(Classes, "Repo")).Code);
        Assert.Equal(ErrorCodes.B1CutNotTestable, Assert.IsType<LocateResult.Failed>(ClassLocator.Locate(Classes, "Audited")).Code);

        // Abstract classes, enums and records run with a warning.
        Assert.NotNull(Assert.IsType<LocateResult.Found>(ClassLocator.Locate(Classes, "Base")).Warning);
        Assert.NotNull(Assert.IsType<LocateResult.Found>(ClassLocator.Locate(Classes, "Status")).Warning);
        Assert.NotNull(Assert.IsType<LocateResult.Found>(ClassLocator.Locate(Classes, "Money")).Warning);
        Assert.Null(Assert.IsType<LocateResult.Found>(ClassLocator.Locate(Classes, "Order")).Warning);
    }

    [Fact]
    public void NestedClassesAndTestFilesAreRejected()
    {
        var nested = Assert.IsType<LocateResult.Failed>(ClassLocator.Locate(Classes, "com.shop.OrderService.Line"));
        Assert.Equal((ErrorCodes.B1CutNotTestable, "com.shop.OrderService"), (nested.Code, nested.Suggestions[0]));
        Assert.Equal(ErrorCodes.B1CutNotTestable, Assert.IsType<LocateResult.Failed>(ClassLocator.Locate(Classes, "com.shop.OrderService$Line")).Code);

        var testFile = Assert.IsType<LocateResult.Failed>(ClassLocator.Locate(Classes, "core/src/test/java/com/shop/OrderServiceTest.java"));
        Assert.Equal(ErrorCodes.B1CutNotTestable, testFile.Code);
        Assert.Equal(ErrorCodes.B1CutNotTestable, Assert.IsType<LocateResult.Failed>(ClassLocator.Locate(Classes, "it/Foo.java", ["it"])).Code);
    }

    [Fact]
    public void PackageListsConcreteClassesWithoutSubPackages()
    {
        Assert.Equal(["com.shop.Helper", "com.shop.Order", "com.shop.OrderService"], ClassLocator.InPackage(Classes, "com.shop").Select(c => c.Fqn));
        Assert.Empty(ClassLocator.InPackage(Classes, "com"));
    }

    private static ClassCandidate Found(LocateResult result) => Assert.IsType<LocateResult.Found>(result).Class;
}
