using System.Xml.Linq;
using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Verification;
using Arrow.DataAccess.Build;

namespace Arrow.Business.Tests.Repair;

/// <summary>Classification and signatures of block 3 (P04 §3.3–3.5, §4.1). Parity with Block3ClassificationTests of ARROW_Framework.</summary>
public class ClassificationTests
{
    private const string Fqn = "com.acme.shop.OrderService_ArrowTest";

    private static readonly string[] Generated = ["OrderService_ArrowTest.java"];

    private static readonly ProjectScope Scope = new(["com.acme.shop", "com.acme.shop.api"], "com.acme.shop", Fqn);

    // ── P04 §3.3: javac errors ─────────────────────────────────────────

    // Parity: Block3ClassificationTests.ParsesJavacErrorsOnEveryPathStyle
    [Fact]
    public void ParsesJavacErrorsOnEveryPathStyle()
    {
        const string log = """
            [INFO] --- maven-compiler-plugin:3.13.0:testCompile (default-testCompile) @ shop-core ---
            [WARNING] /Users/a/ws/shop-core/src/test/java/com/acme/shop/OldTest.java:[3,8] [deprecation] Foo in bar has been deprecated
            [ERROR] COMPILATION ERROR :
            [ERROR] /Users/a/ws/shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java:[14,30] cannot find symbol
              symbol:   method getTotal(long)
              location: variable service of type com.acme.shop.OrderService
            [ERROR] /D:/ws/shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java:[20,12] incompatible types: java.lang.String cannot be converted to long
            [ERROR] Failed to execute goal org.apache.maven.plugins:maven-compiler-plugin:3.13.0:testCompile (default-testCompile) on project shop-core: Compilation failure: Compilation failure:
            [ERROR] /Users/a/ws/shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java:[14,30] cannot find symbol
            [ERROR]   symbol:   method getTotal(long)
            [ERROR]   location: variable service of type com.acme.shop.OrderService
            D:\ws\core\src\test\java\com\acme\Helper.java:7: error: package org.junit.jupiter.api does not exist
            import org.junit.jupiter.api.Test;
                                        ^
            /home/u/ws/core/src/test/java/com/acme/OrderService_ArrowTest.java:31: error: constructor OrderService in class OrderService cannot be applied to given types;
                    new OrderService();
                    ^
              required: TaxPolicy
              found:    no arguments
            /home/u/ws/core/src/test/java/com/acme/OrderService_ArrowTest.java:40: warning: [unchecked] unchecked call
                at com.acme.OrderService.totalPrice(OrderService.java:31)
            """;
        var errors = JavacErrorParser.Parse(log, Generated);
        Assert.Equal(4, errors.Count); // Maven prints each error twice; warnings and stack frames are not errors

        Assert.Equal((14, 30, "cannot find symbol", "method", "getTotal(long)"), (errors[0].Line, errors[0].Column!.Value, errors[0].Kind, errors[0].SymbolKind!, errors[0].Symbol!));
        Assert.Equal("variable service of type com.acme.shop.OrderService", errors[0].Location);
        Assert.True(errors[0].InGeneratedFile);
        Assert.Equal(("/D:/ws/shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java", "incompatible types"), (errors[1].File, errors[1].Kind));
        Assert.Equal((7, (int?)null, "package does not exist", false), (errors[2].Line, errors[2].Column, errors[2].Kind, errors[2].InGeneratedFile));
        Assert.Equal(("constructor cannot be applied", 31), (errors[3].Kind, errors[3].Line));
    }

    [Theory]
    [InlineData("/D:/ws/copy-1/shop-core/src/test/java/X.java", "D:\\ws\\copy-1", "shop-core/src/test/java/X.java")]
    [InlineData("D:\\ws\\copy-1\\core\\src\\test\\java\\X.java", "D:\\ws\\copy-1", "core/src/test/java/X.java")]
    [InlineData("/work/ws/copy-1/src/test/java/X.java", "/work/ws/copy-1/", "src/test/java/X.java")]
    [InlineData("/elsewhere/X.java", "/work/ws/copy-1", "/elsewhere/X.java")]
    public void MakesFilesUnderTheWorkspaceRelative(string file, string root, string expected) => Assert.Equal(expected, JavacErrorParser.Relative(file, root));

    [Fact]
    public void ParsedFilesHaveNoWorkspacePath()
    {
        const string log = "[ERROR] /D:/ws/copy-1/shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java:[14,30] cannot find symbol";
        var error = Assert.Single(JavacErrorParser.Parse(log, Generated, "D:\\ws\\copy-1"));
        Assert.Equal("shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java", error.File);
        Assert.True(error.InGeneratedFile);
    }

    // R03 §3.2 step 5: maven-compiler-plugin 2.x prints "error:" after the position, no "symbol:" line and a bare location.
    [Fact]
    public void ReadsTheCutDownFormatOfMavenCompilerPlugin2()
    {
        const string log = """
            [INFO] --- maven-compiler-plugin:2.3.2:testCompile (default-testCompile) @ commons-geometry ---
            [INFO] Compiling 1 source file to /work/ws/copy-1/target/test-classes
            [INFO] -------------------------------------------------------------
            [ERROR] COMPILATION ERROR :
            [INFO] -------------------------------------------------------------
            [ERROR] /work/ws/copy-1/src/test/java/org/geo/Bounds_ArrowTest.java:[65,44] error: cannot find symbol
            [ERROR]  class Bounds
            [ERROR] /work/ws/copy-1/src/test/java/org/geo/Bounds_ArrowTest.java:[70,9] error: incompatible types
            [INFO] 2 errors
            """;
        var errors = JavacErrorParser.Parse(log, ["Bounds_ArrowTest.java"], "/work/ws/copy-1");
        Assert.Equal(2, errors.Count);
        Assert.Equal(("cannot find symbol", "cannot find symbol", "class Bounds"), (errors[0].Message, errors[0].Kind, errors[0].Location));
        Assert.Null(errors[0].Symbol);
        Assert.Null(errors[0].SymbolKind);
        Assert.Equal(("src/test/java/org/geo/Bounds_ArrowTest.java", 65, 44), (errors[0].File, errors[0].Line, errors[0].Column!.Value));
        Assert.Equal(("incompatible types", null), (errors[1].Kind, errors[1].Location));
        Assert.Equal("COMPILE | cannot find symbol | -", SignatureBuilder.Compile(errors));
    }

    // P04 §3.3 step 4: under -Werror the generated test only has warnings, so no javac error is in it → UNKNOWN.
    [Fact]
    public void WarningsUnderWerrorAreNotJavacErrors()
    {
        const string maven = """
            [WARNING] /w/src/test/java/com/acme/shop/OrderService_ArrowTest.java:[12,20] [deprecation] getRate() in TaxPolicy has been deprecated
            [ERROR] COMPILATION ERROR :
            [ERROR] warnings found and -Werror specified
            [ERROR] /w/src/test/java/com/acme/shop/OrderService_ArrowTest.java:[12,20] [deprecation] getRate() in TaxPolicy has been deprecated
            [ERROR] Failed to execute goal org.apache.maven.plugins:maven-compiler-plugin:3.13.0:testCompile (default-testCompile) on project shop: Compilation failure
            """;
        const string gradle = """
            /w/src/test/java/com/acme/shop/OrderService_ArrowTest.java:12: warning: [deprecation] getRate() in TaxPolicy has been deprecated
                    long rate = tax.getRate();
                                   ^
            error: warnings found and -Werror specified
            1 error
            1 warning
            """;
        Assert.Empty(JavacErrorParser.Parse(maven, Generated));
        Assert.Empty(JavacErrorParser.Parse(gradle, Generated));
        Assert.Equal("UNKNOWN | maven-compiler-plugin | -", SignatureBuilder.Unknown(maven));
    }

    // Parity: Block3ClassificationTests.NormalizesJavacKinds
    [Theory]
    [InlineData("cannot find symbol", "cannot find symbol")]
    [InlineData("incompatible types: String cannot be converted to long", "incompatible types")]
    [InlineData("unreported exception java.io.IOException; must be caught or declared to be thrown", "unreported exception")]
    [InlineData("method apply in class TaxPolicy cannot be applied to given types;", "method cannot be applied")]
    [InlineData("no suitable method found for of(int)", "method cannot be applied")]
    [InlineData("constructor OrderService in class OrderService cannot be applied to given types;", "constructor cannot be applied")]
    [InlineData("no suitable constructor found for OrderService(int)", "constructor cannot be applied")]
    [InlineData("package org.assertj.core.api does not exist", "package does not exist")]
    [InlineData("tax has private access in com.acme.shop.OrderService", "has private access")]
    [InlineData("com.acme.Internal is not public in com.acme; cannot be accessed from outside package", "is not public")]
    [InlineData("non-static method total() cannot be referenced from a static context", "cannot be referenced from a static context")]
    [InlineData("variable service is already defined in method setUp()", "already defined")]
    [InlineData("missing return statement", "missing return statement")]
    [InlineData("unreachable statement", "unreachable statement")]
    [InlineData("com.acme.Shape is abstract; cannot be instantiated", "class is abstract")]
    [InlineData("method does not override or implement a method from a supertype", "does not override")]
    [InlineData("cannot access com.acme.Hidden", "cannot be accessed")]
    [InlineData("';' expected", "other")]
    public void NormalizesJavacKinds(string message, string kind) => Assert.Equal(kind, JavacErrorParser.Kind(message));

    // ── P04 §3.4: the run of the generated class ──────────────────────

    private static TestCaseResult T(string name, TestCaseStatus status, string? type = null, string? message = null, string? trace = null, string cls = Fqn) =>
        new(cls, name, status, message, type, trace);

    private const string NpeTrace = """
        java.lang.NullPointerException: Cannot invoke "com.acme.shop.api.TaxPolicy.apply(long)" because "this.tax" is null
        	at com.acme.shop.OrderService.totalPrice(OrderService.java:31)
        	at com.acme.shop.OrderService_ArrowTest.addsTax(OrderService_ArrowTest.java:14)
        	at java.base/jdk.internal.reflect.NativeMethodAccessorImpl.invoke0(Native Method)
        """;

    // Parity: Block3ClassificationTests.ClassifiesDiscoveryRuntimeAssertionAndPass
    [Fact]
    public void ClassifiesDiscoveryRuntimeAssertionAndPass()
    {
        Assert.Equal(VerificationState.Discovery, TestResultClassifier.Classify([T("other", TestCaseStatus.Passed, cls: "com.acme.OtherTest")], Fqn, Scope).FailedState);
        Assert.Equal(VerificationState.Discovery, TestResultClassifier.Classify([T("initializationError", TestCaseStatus.Error, "java.lang.Exception", "No runnable methods")], Fqn, Scope).FailedState);
        Assert.Equal(VerificationState.Discovery, TestResultClassifier.Classify([T("skipped", TestCaseStatus.Skipped)], Fqn, Scope).FailedState);

        var runtime = TestResultClassifier.Classify([T("addsTax", TestCaseStatus.Error, "java.lang.NullPointerException", trace: NpeTrace), T("rejects", TestCaseStatus.Failed, "java.lang.AssertionError"), T("ok", TestCaseStatus.Passed)], Fqn, Scope);
        Assert.Equal(VerificationState.Runtime, runtime.FailedState);
        Assert.Equal(new TestMethodCounts(3, 1, 2), runtime.Counts);
        Assert.Equal(["addsTax", "rejects"], runtime.FailingMethods);
        Assert.Equal("OrderService.totalPrice", runtime.Failures[0].Frame);
        Assert.Equal(4, runtime.Failures[0].TraceHead!.Split('\n').Length);

        var assertion = TestResultClassifier.Classify([T("totalPriceAddsTax", TestCaseStatus.Failed, "org.opentest4j.AssertionFailedError", "expected: <110> but was: <100>"), T("ok", TestCaseStatus.Passed)], Fqn, Scope);
        Assert.Equal(VerificationState.Assertion, assertion.FailedState);
        Assert.Equal(["totalPriceAddsTax"], assertion.FailingMethods);

        // A failure to initialize the class (other than "No runnable methods") is RUNTIME; it names no method to rewrite.
        var init = TestResultClassifier.Classify([T("initializationError", TestCaseStatus.Error, "java.lang.ExceptionInInitializerError")], Fqn, Scope);
        Assert.Equal(VerificationState.Runtime, init.FailedState);
        Assert.Empty(init.FailingMethods);

        var pass = TestResultClassifier.Classify([T("a", TestCaseStatus.Passed), T("b", TestCaseStatus.Passed, cls: Fqn + "$Nested")], Fqn, Scope);
        Assert.Null(pass.FailedState);
        Assert.Equal(2, pass.Counts.Passed);
    }

    // P04 §12 item 7: XML samples for DISCOVERY, RUNTIME and ASSERTION, with a message in "type".
    [Fact]
    public void ClassifiesReportsReadFromXml()
    {
        static IReadOnlyList<TestCaseResult> Xml(string body) =>
            TestReportReader.Parse(XDocument.Parse($"""<testsuite name="{Fqn}">{body}</testsuite>"""));

        var discovery = Xml($"""
            <testcase name="initializationError" classname="{Fqn}">
              <error message="No runnable methods" type="java.lang.Exception">java.lang.Exception: No runnable methods
            	at org.junit.runners.BlockJUnit4ClassRunner.validate(BlockJUnit4ClassRunner.java:1)</error>
            </testcase>
            """);
        Assert.Equal(VerificationState.Discovery, TestResultClassifier.Classify(discovery, Fqn, Scope).FailedState);

        var runtime = Xml($"""
            <testcase name="addsTax()" classname="{Fqn}">
              <error message="amount &lt; 0" type="amount &lt; 0">java.lang.IllegalArgumentException: amount &lt; 0
            	at com.acme.shop.Money.of(Money.java:12)
            	at com.acme.shop.OrderService_ArrowTest.addsTax(OrderService_ArrowTest.java:20)</error>
            </testcase>
            """);
        var r = TestResultClassifier.Classify(runtime, Fqn, Scope);
        Assert.Equal(VerificationState.Runtime, r.FailedState);
        Assert.Equal(("addsTax", "java.lang.IllegalArgumentException", "Money.of"), (r.Failures[0].Method, r.Failures[0].Type, r.Failures[0].Frame));
        Assert.Equal("RUNTIME | IllegalArgumentException | Money.of", SignatureBuilder.Runtime(r.Failures));

        var assertion = Xml($"""
            <testcase name="totalPriceAddsTax" classname="{Fqn}">
              <failure message="expected:&lt;110&gt; but was:&lt;100&gt;" type="expected:&lt;110&gt; but was:&lt;100&gt;">java.lang.AssertionError: expected:&lt;110&gt; but was:&lt;100&gt;
            	at org.junit.Assert.fail(Assert.java:89)</failure>
            </testcase>
            <testcase name="ok" classname="{Fqn}"/>
            """);
        var a = TestResultClassifier.Classify(assertion, Fqn, Scope);
        Assert.Equal(VerificationState.Assertion, a.FailedState);
        Assert.Equal(new TestMethodCounts(2, 1, 1), a.Counts);
        Assert.Equal("ASSERTION | AssertionError | totalPriceAddsTax", SignatureBuilder.Assertion(a.Failures));
    }

    // Parity: Block3ClassificationTests.RecognizesAssertionExceptions, plus the "Expected … to be thrown" failure of assertThrows.
    [Theory]
    [InlineData("java.lang.AssertionError", null, true)]
    [InlineData("org.opentest4j.AssertionFailedError", null, true)]
    [InlineData("org.junit.ComparisonFailure", null, true)]
    [InlineData("junit.framework.AssertionFailedError", null, true)]
    [InlineData("org.opentest4j.MultipleFailuresError", null, true)]
    [InlineData("org.assertj.core.error.AssertJMultipleFailuresError", null, true)]
    [InlineData("org.mockito.exceptions.verification.WantedButNotInvoked", null, true)]
    [InlineData("org.hamcrest.AssertionError", null, true)]
    [InlineData("org.testng.TestException", "Method OrderServiceTest.rejects() should have thrown an exception of class IllegalStateException", true)]
    [InlineData("org.testng.TestException", "Cannot instantiate class", false)]
    [InlineData("com.acme.CustomFailure", "Expected java.lang.IllegalStateException to be thrown, but nothing was thrown.", true)]
    [InlineData("java.lang.NullPointerException", null, false)]
    [InlineData("org.mockito.exceptions.misusing.UnfinishedStubbingException", null, false)]
    public void RecognizesAssertionExceptions(string type, string? message, bool expected) => Assert.Equal(expected, TestResultClassifier.IsAssertion(type, message));

    // Parity: Block3ClassificationTests.ExceptionNameFallsBackToTraceWhenTypeIsAMessage
    [Fact]
    public void ExceptionNameFallsBackToTraceWhenTypeIsAMessage()
    {
        Assert.Equal("java.lang.IllegalStateException", TestResultClassifier.ExceptionName("java.lang.IllegalStateException", null));
        Assert.Equal("java.lang.IllegalArgumentException", TestResultClassifier.ExceptionName("amount < 0", "java.lang.IllegalArgumentException: amount < 0\n\tat x.Y.z(Y.java:1)"));
        Assert.Equal("java.io.IOException", TestResultClassifier.ExceptionName("wrapped", "something odd\n\tat a.B.c(B.java:1)\nCaused by: java.io.IOException: disk"));
        Assert.Equal("unknown", TestResultClassifier.ExceptionName(null, null));
        var scope = new ProjectScope(["com.acme"], "com.acme", "com.acme.Parser_ArrowTest");
        Assert.Equal("Parser$Inner.lambda$parse$0", TestResultClassifier.ProjectFrame("e\n\tat app//com.acme.Parser$Inner.lambda$parse$0(Parser.java:9)", scope));
        Assert.Null(TestResultClassifier.ProjectFrame("java.lang.IllegalStateException: no frame", scope));
    }

    // P04 §4.1 (differs from ARROW_Framework, which skipped library frames and gave null): the first frame in the project
    // packages or under the CUT package, not the generated test; without one, the first frame.
    [Fact]
    public void PicksTheFirstProjectFrame()
    {
        const string trace = """
            java.lang.IllegalStateException: closed
            	at java.base/java.util.Objects.requireNonNull(Objects.java:208)
            	at com.google.common.base.Preconditions.checkState(Preconditions.java:502)
            	at com.acme.shop.OrderService_ArrowTest.helper(OrderService_ArrowTest.java:40)
            	at com.acme.shop.impl.Cart.close(Cart.java:17)
            	at com.acme.shop.OrderService.checkout(OrderService.java:55)
            """;
        Assert.Equal("Cart.close", TestResultClassifier.ProjectFrame(trace, Scope));

        // A class of project_structure.packages outside the CUT package counts too.
        var other = new ProjectScope(["com.acme.util"], "com.acme.shop", Fqn);
        Assert.Equal("Strings.trim", TestResultClassifier.ProjectFrame("x\n\tat java.lang.String.trim(String.java:1)\n\tat com.acme.util.Strings.trim(Strings.java:3)", other));

        // Only JDK and test frames: the first frame.
        Assert.Equal("Objects.requireNonNull", TestResultClassifier.ProjectFrame(
            "java.lang.X\n\tat java.base/java.util.Objects.requireNonNull(Objects.java:208)\n\tat org.junit.Assert.fail(Assert.java:1)", Scope));
        Assert.Equal("OrderService_ArrowTest.helper", TestResultClassifier.ProjectFrame(
            "java.lang.X\n\tat com.acme.shop.OrderService_ArrowTest.helper(OrderService_ArrowTest.java:40)", Scope));
    }

    // ── P04 §3.5: module regression ───────────────────────────────────

    // Parity: Block3ClassificationTests.FindsRegressions (kinds added by P04 §3.8)
    [Fact]
    public void FindsRegressions()
    {
        string[] baseline = ["com.acme.InvoiceTest#appliesDefaultTax", "com.acme.CartTest#adds", "com.acme.CartTest#removes"];
        IReadOnlyList<TestCaseResult> module =
        [
            T("appliesDefaultTax", TestCaseStatus.Failed, "java.lang.AssertionError", "expected:<10> but was:<20>", cls: "com.acme.InvoiceTest"),
            T("adds", TestCaseStatus.Passed, cls: "com.acme.CartTest"),
            T("now", TestCaseStatus.Failed, "java.lang.AssertionError", cls: "com.acme.ClockTest"), // flaky at baseline: not considered
            T("addsTax", TestCaseStatus.Passed),
        ];
        var check = RegressionChecker.Check(baseline, module, Fqn);
        Assert.Equal(["com.acme.InvoiceTest#appliesDefaultTax", "com.acme.CartTest#removes"], check.Regressions.Select(r => r.Test));
        Assert.Equal([RegressionKind.Failed, RegressionKind.Missing], check.Regressions.Select(r => r.Kind));
        Assert.Equal(["java.lang.AssertionError", null], check.Regressions.Select(r => r.Type));
        Assert.True(check.GeneratedTestRan);
        Assert.Equal("REGRESSION | existing tests fail | CartTest,InvoiceTest", SignatureBuilder.Regression(check.Regressions));

        // R5, case (c): the generated test passes alone but fails with the module.
        var own = RegressionChecker.Check([], [T("addsTax", TestCaseStatus.Failed, "java.lang.AssertionError")], Fqn);
        Assert.Equal(RegressionKind.GeneratedFailedInModule, Assert.Single(own.Regressions).Kind);
        Assert.Equal("REGRESSION | existing tests fail | OrderService_ArrowTest", SignatureBuilder.Regression(own.Regressions));

        Assert.Empty(RegressionChecker.Check(["com.acme.CartTest#adds"], [T("adds", TestCaseStatus.Passed, cls: "com.acme.CartTest"), T("x", TestCaseStatus.Passed)], Fqn).Regressions);
    }

    [Fact]
    public void AGeneratedTestThatDidNotRunInTheModuleIsNotARegression()
    {
        var check = RegressionChecker.Check(["com.acme.CartTest#adds"], [T("adds", TestCaseStatus.Passed, cls: "com.acme.CartTest")], Fqn);
        Assert.Empty(check.Regressions);
        Assert.False(check.GeneratedTestRan);
    }

    [Fact]
    public void ATestReportedTwiceCountsWithItsWorstStatus()
    {
        IReadOnlyList<TestCaseResult> module =
        [
            T("adds", TestCaseStatus.Passed, cls: "com.acme.CartTest"),
            T("adds", TestCaseStatus.Error, "java.lang.IllegalStateException", "shared state", cls: "com.acme.CartTest"),
            T("addsTax", TestCaseStatus.Passed),
            T("addsTax", TestCaseStatus.Passed),
        ];
        var regression = Assert.Single(RegressionChecker.Check(["com.acme.CartTest#adds"], module, Fqn).Regressions);
        Assert.Equal(("com.acme.CartTest#adds", "java.lang.IllegalStateException"), (regression.Test, regression.Type));
    }

    [Fact]
    public void RegressionSignatureDropsNestedClasses()
    {
        RegressionInfo[] regressions =
        [
            new("com.acme.TotalsTest$Rounding#halfUp", RegressionKind.Failed, "java.lang.AssertionError"),
            new("com.acme.InvoiceTest#tax", RegressionKind.Missing),
            new("com.acme.TotalsTest#sum", RegressionKind.Failed, "java.lang.AssertionError"),
        ];
        Assert.Equal("REGRESSION | existing tests fail | InvoiceTest,TotalsTest", SignatureBuilder.Regression(regressions));
    }

    // ── P04 §4.1: signatures ──────────────────────────────────────────

    // Parity: Block3ClassificationTests.BuildsSignaturesForEveryState
    [Fact]
    public void BuildsSignaturesForEveryState()
    {
        CompileError[] compile =
        [
            new("Helper.java", 1, null, "package x does not exist", "package does not exist"),
            new("OrderService_ArrowTest.java", 2, null, "cannot find symbol", "cannot find symbol", "method", "getTotal(long)", null, InGeneratedFile: true),
        ];
        Assert.Equal("COMPILE | cannot find symbol | method", SignatureBuilder.Compile(compile));
        Assert.Equal("COMPILE | incompatible types | -", SignatureBuilder.Compile([new CompileError("X.java", 1, null, "incompatible types", "incompatible types")]));
        Assert.Equal("COMPILE | other | -", SignatureBuilder.Compile([new CompileError("X.java", 1, null, "';' expected", JavacErrorParser.Kind("';' expected"))]));
        Assert.Equal("UNKNOWN | maven-enforcer-plugin | -", SignatureBuilder.Unknown(MavenEnforcerLog));
        Assert.Equal("UNKNOWN | compileTestJava | -", SignatureBuilder.Unknown(GradleLog));
        Assert.Equal("DISCOVERY | no tests executed | -", SignatureBuilder.Discovery());

        var npe = TestResultClassifier.ToFailure(T("addsTax", TestCaseStatus.Error, "java.lang.NullPointerException", trace: NpeTrace), Scope);
        Assert.Equal("RUNTIME | NullPointerException | OrderService.totalPrice", SignatureBuilder.Runtime([npe]));
        var failed = TestResultClassifier.ToFailure(T("testGetOneNotFound", TestCaseStatus.Failed, "org.opentest4j.AssertionFailedError"), Scope);
        Assert.Equal("ASSERTION | AssertionFailedError | testGetOneNotFound", SignatureBuilder.Assertion([failed]));
        Assert.Equal("REGRESSION | existing tests fail | InvoiceTest", SignatureBuilder.Regression([new RegressionInfo("com.acme.InvoiceTest#t", RegressionKind.Missing)]));

        // P04 §3.2: the run of step 2 crashed without a report.
        Assert.Equal("RUNTIME | maven-surefire-plugin | -", SignatureBuilder.RuntimeCrash(
            "[ERROR] Failed to execute goal org.apache.maven.plugins:maven-surefire-plugin:3.2.5:test (default-test) on project shop: The forked VM terminated without properly saying goodbye."));
    }

    private const string MavenEnforcerLog = """
        [INFO] BUILD FAILURE
        [INFO] ------------------------------------------------------------------------
        [ERROR] Failed to execute goal org.apache.maven.plugins:maven-enforcer-plugin:3.0.0:enforce (enforce-versions) on project shop-core:
        [ERROR] Rule 0: org.apache.maven.plugins.enforcer.RequireJavaVersion failed with message:
        [ERROR] Detected JDK /work/ws/copy-1/jdk is version 1.8.0 which is not in the allowed range [11,).
        [ERROR] Rule 0: org.apache.maven.plugins.enforcer.RequireJavaVersion failed with message:
        [ERROR] -> [Help 1]
        [ERROR]
        [ERROR] To see the full stack trace of the errors, re-run Maven with the -e switch.
        """;

    private const string GradleLog = """
        > Task :core:compileTestJava FAILED

        FAILURE: Build failed with an exception.

        * What went wrong:
        Execution failed for task ':core:compileTestJava'.
        > error: annotation processor 'lombok' threw /work/ws/copy-1/core/Gen.java
          Caused by: java.lang.IllegalStateException: boom

        * Try:
        > Run with --stacktrace option to get the stack trace.

        BUILD FAILED in 3s
        """;

    // Parity: Block3ClassificationTests.ExtractsBuildErrorBlocks
    [Fact]
    public void ExtractsBuildErrorBlocks()
    {
        var maven = BuildErrorExtractor.Excerpt(MavenEnforcerLog, "/work/ws/copy-1");
        Assert.Equal("""
            [ERROR] Failed to execute goal org.apache.maven.plugins:maven-enforcer-plugin:3.0.0:enforce (enforce-versions) on project shop-core:
            [ERROR] Rule 0: org.apache.maven.plugins.enforcer.RequireJavaVersion failed with message:
            [ERROR] Detected JDK jdk is version 1.8.0 which is not in the allowed range [11,).
            """.Replace("\r\n", "\n", StringComparison.Ordinal), maven);

        var gradle = BuildErrorExtractor.Excerpt(GradleLog, "/work/ws/copy-1");
        Assert.Contains("Execution failed for task ':core:compileTestJava'.", gradle, StringComparison.Ordinal);
        Assert.Contains("threw core/Gen.java", gradle, StringComparison.Ordinal);
        Assert.Contains("Caused by: java.lang.IllegalStateException: boom", gradle, StringComparison.Ordinal);
        Assert.DoesNotContain("--stacktrace", gradle, StringComparison.Ordinal);
        Assert.Equal("build", BuildErrorExtractor.FailingStep("something else"));
        Assert.Equal(2, BuildErrorExtractor.Excerpt(MavenEnforcerLog, null, maxLines: 2).Split('\n').Length);
    }

    // Parity: Block3ClassificationTests.ReaderKeepsTraceAndStripsGradleParentheses
    [Fact]
    public void ReaderKeepsTraceAndStripsGradleParentheses()
    {
        var tests = TestReportReader.Parse(XDocument.Parse("""
            <testsuite name="com.acme.calc.CalculatorTest">
              <testcase name="adds()" classname="com.acme.calc.CalculatorTest"/>
              <testcase name="divides()" classname="com.acme.calc.CalculatorTest">
                <failure message="/ by zero" type="java.lang.ArithmeticException">java.lang.ArithmeticException: / by zero
            	at com.acme.calc.Calculator.divide(Calculator.java:9)</failure>
              </testcase>
              <testcase name="sum(int)[1]" classname="com.acme.calc.CalculatorTest"/>
            </testsuite>
            """));
        Assert.Equal(["adds", "divides", "sum(int)[1]"], tests.Select(t => t.Name));
        Assert.StartsWith("java.lang.ArithmeticException: / by zero", tests[1].Trace, StringComparison.Ordinal);
        Assert.Null(tests[0].Trace);
        Assert.Equal("Calculator.divide", TestResultClassifier.ProjectFrame(tests[1].Trace, new ProjectScope(["com.acme.calc"], "com.acme.calc", "com.acme.calc.CalculatorTest")));
    }
}
