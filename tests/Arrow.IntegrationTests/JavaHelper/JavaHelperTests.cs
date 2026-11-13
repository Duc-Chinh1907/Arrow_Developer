using Arrow.DataAccess.JavaHelper;

namespace Arrow.IntegrationTests.JavaHelper;

/// <summary>
/// Parity with <c>ARROW_Framework/tests/Arrow.IntegrationTests/JavaHelperTests.cs</c>: same inputs, same expectations.
/// Differences by the P-specs: version 1.1.0; an <c>ok: false</c> answer is a <see cref="JavaHelperCommandException"/> (P10 §5)
/// instead of a coded error.
/// </summary>
[Trait("Category", "Integration")]
public class JavaHelperTests(JavaHelperFixture fixture) : IClassFixture<JavaHelperFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IJavaHelper Helper => fixture.Client;

    private const string Cut = """
        package com.acme.shop;

        public class OrderService {
            private final TaxPolicy tax;
            public OrderService(TaxPolicy tax) { this.tax = tax; }
            public long totalPrice(long orderId) { return tax.apply(100); }
            public void cancel(long orderId) {}
            private void secret() {}
        }
        """;

    private const string Test = """
        import org.junit.Test;
        import static org.junit.Assert.*;

        public class OrderServiceTest {
            private OrderServiceTest self;

            @Test
            public void totalPriceAddsTax() {
                OrderService service = new OrderService(new TaxPolicy(10));
                assertEquals(110, service.getTotal(1L));
                assertTrue(true);
            }

            @Test(expected = IllegalStateException.class)
            public void cancelTwiceFails() { new OrderService(null).cancel(1L); }
        }
        """;

    /// <summary>Parity: <c>PingParseAndNormalizeNames</c>.</summary>
    [Fact]
    public async Task PingParseAndNormalizeNames()
    {
        Assert.Equal(JavaHelperJar.Version, await Helper.PingAsync(Ct));

        var parsed = await Helper.ParseAsync(Test, Ct);
        Assert.True(parsed.Parsed);
        Assert.Equal("", parsed.Package);
        Assert.Equal("OrderServiceTest", Assert.Single(parsed.TopLevelTypes).Name);
        Assert.Equal(2, parsed.TestMethodCount);
        Assert.False((await Helper.ParseAsync("class A { void x( }", Ct)).Parsed);

        var renamed = await Helper.RenameClassAsync(Test, "OrderService_ArrowTest", "com.acme.shop", Ct);
        Assert.StartsWith("package com.acme.shop;", renamed.Code, StringComparison.Ordinal);
        Assert.Contains("public class OrderService_ArrowTest {", renamed.Code, StringComparison.Ordinal);
        Assert.Contains("private OrderService_ArrowTest self;", renamed.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderServiceTest", renamed.Code, StringComparison.Ordinal);
        Assert.Equal(["package: added com.acme.shop", "class: OrderServiceTest → OrderService_ArrowTest"], renamed.Actions);
    }

    /// <summary>Parity: <c>HashCountsTestsAndLines</c>, plus the assertion kinds of R03 §3.6 added in java-helper 1.1.0.</summary>
    [Fact]
    public async Task HashCountsTestsAndLines()
    {
        var a = await Helper.StripCommentsHashAsync("class A { /* x */ int f() { return 1; } }", Ct);
        var b = await Helper.StripCommentsHashAsync("class A {\n  // y\n  int f() {\n    return 1;\n  }\n}\n", Ct);
        Assert.Equal(a.CodeHash, b.CodeHash);
        Assert.StartsWith("sha256:", a.CodeHash, StringComparison.Ordinal);

        var counts = await Helper.CountAssertionsAsync(Test, Ct);
        Assert.Equal(2, counts.Total); // assertEquals + @Test(expected); assertTrue(true) is trivial
        Assert.Equal(1, counts.PerMethod["totalPriceAddsTax"]);
        Assert.Equal(1, counts.PerMethod["cancelTwiceFails"]);

        Assert.Equal(["totalPriceAddsTax", "cancelTwiceFails"], await Helper.ListTestMethodsAsync(Test, Ct));

        var lines = await Helper.MethodAtLineAsync(Test, [10, 1], Ct);
        Assert.Equal("totalPriceAddsTax", lines[10]);
        Assert.Null(lines[1]);

        const string mockito = """
            class T {
                @org.junit.Test public void t() {
                    inOrder.verify(repo).save(o);
                    verifyNoMoreInteractions(repo);
                    then(repo).should().find(1L);
                    mvc.perform(get("/")).andExpect(status().isOk());
                    thrown.expect(IllegalStateException.class);
                }
            }
            """;
        Assert.Equal(5, (await Helper.CountAssertionsAsync(mockito, Ct)).PerMethod["t"]);
    }

    /// <summary>Parity: <c>SpliceKeepsOtherMethodsAndReportsChanges</c>.</summary>
    [Fact]
    public async Task SpliceKeepsOtherMethodsAndReportsChanges()
    {
        const string response = """
            import org.junit.Test;
            import java.util.List;
            import static org.junit.Assert.*;

            public class OrderServiceTest {
                @Test
                public void totalPriceAddsTax() {
                    assertEquals(110, new OrderService(new TaxPolicy(10)).totalPrice(1L));
                }
            }
            """;
        var result = await Helper.SpliceMethodsAsync(Test, response, ["totalPriceAddsTax"], Ct);
        Assert.Equal(["totalPriceAddsTax"], result.Replaced);
        Assert.Empty(result.Dropped);
        Assert.Equal(["import java.util.List;"], result.AddedImports);
        Assert.Contains(".totalPrice(1L)", result.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("getTotal", result.Code, StringComparison.Ordinal);
        Assert.Contains("public void cancelTwiceFails() { new OrderService(null).cancel(1L); }", result.Code, StringComparison.Ordinal);

        // An answer that does not parse is a command error, and the same process keeps working.
        var error = await Assert.ThrowsAsync<JavaHelperCommandException>(() => Helper.SpliceMethodsAsync(Test, "class {", ["totalPriceAddsTax"], Ct));
        Assert.Equal("splice_methods", error.Command);
        Assert.True((await Helper.ParseAsync(Test, Ct)).Parsed);
    }

    /// <summary>Parity: <c>ClassApiFromSourceAndFromJarOnClasspath</c> (the java-helper jar itself is the library on the classpath).</summary>
    [Fact]
    public async Task ClassApiFromSourceAndFromJarOnClasspath()
    {
        var jar = JavaHelperEnvironment.Options().JarPath;
        var api = await Helper.ClassApiAsync([Cut], ["com.google.gson.Gson", "com.example.Missing"], [jar], Ct);
        var order = api.Types.Single(t => t.Name == "com.acme.shop.OrderService");
        Assert.Equal("source", order.Origin);
        Assert.Contains("public OrderService(TaxPolicy tax)", order.Members);
        Assert.Contains("public long totalPrice(long orderId)", order.Members);
        Assert.DoesNotContain(order.Members, m => m.Contains("secret", StringComparison.Ordinal));

        var gson = api.Types.Single(t => t.Name == "com.google.gson.Gson");
        Assert.Equal("classpath", gson.Origin);
        Assert.Contains(gson.Members, m => m.StartsWith("public String toJson(Object)", StringComparison.Ordinal));
        Assert.Equal("com.example.Missing", Assert.Single(api.Missing).Name);
    }

    /// <summary>Parity: <c>MethodSourcesAndCalledMethods</c>.</summary>
    [Fact]
    public async Task MethodSourcesAndCalledMethods()
    {
        var sources = await Helper.MethodSourceAsync(Cut, ["totalPrice", "<init>"], Ct);
        Assert.Equal(["<init>", "totalPrice"], sources.Select(m => m.Name));
        Assert.StartsWith("public long totalPrice(long orderId)", sources[1].Source, StringComparison.Ordinal);

        const string test = "class T { @Test void a() { s.totalPrice(1L); s.getTotal(1L); } @Test void b() { s.cancel(1L); } }";
        var called = await Helper.CalledMethodsAsync(test, ["a"], Cut, Ct);
        Assert.Equal(["totalPrice"], called.Called);
        Assert.Single(called.Methods);
        Assert.Single(called.Constructors);
        Assert.Equal(["private final TaxPolicy tax;"], called.Fields);
    }

    /// <summary>
    /// Parity: <c>ParallelCallsAreServedOneByOne</c>. Within one process commands are still sequential (P10 §5);
    /// parallel jobs get their own processes from <see cref="JavaHelperPool"/> (see <see cref="JavaHelperProcessTests"/>).
    /// </summary>
    [Fact]
    public async Task ParallelCallsAreServedOneByOne()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            Helper.ParseAsync($"class C{i} {{ @Test void t{i}() {{}} }}", Ct)));
        Assert.All(results, r => Assert.True(r.Parsed));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => $"C{i}"), results.Select(r => r.TopLevelTypes[0].Name));
    }

    /// <summary>Parity: <c>RestartsAfterProcessIsKilled</c>.</summary>
    [Fact]
    public async Task RestartsAfterProcessIsKilled()
    {
        await Helper.PingAsync(Ct);
        var pid = fixture.Client.ProcessId;
        Assert.NotNull(pid);
        using (var process = System.Diagnostics.Process.GetProcessById(pid!.Value))
        {
            process.Kill();
            process.WaitForExit(5_000);
        }

        Assert.Equal(JavaHelperJar.Version, await Helper.PingAsync(Ct));
        Assert.NotEqual(pid, fixture.Client.ProcessId);
    }

    [Fact]
    public async Task ShrinkAndListClassesWork()
    {
        var shrunk = await Helper.ShrinkSourceAsync(Cut, 50_000, Ct);
        Assert.Equal(("none", Cut), (shrunk.Truncation, shrunk.Code));

        var root = Path.Combine(Fixtures.FixtureRepo.FixturesDir, "maven-multi-junit5", "shop-core", "src", "main", "java");
        var classes = await Helper.ListClassesAsync([root], 100, Ct);
        var order = Assert.Single(classes.Classes);
        Assert.Equal(("com.acme.shop.OrderService", Arrow.Common.CutKind.Class, true), (order.Fqn, order.Kind, order.IsPublic));
        Assert.Empty(classes.Errors);
    }
}
