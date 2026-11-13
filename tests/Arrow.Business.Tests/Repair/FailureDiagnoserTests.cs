using Arrow.Business.Repair;
using Arrow.Common;
using Arrow.Common.Analysis;
using Arrow.Common.Generation;
using Arrow.Common.Verification;

namespace Arrow.Business.Tests.Repair;

/// <summary>Diagnosis, error block and first error of every state (P04 §4). Parity with FailureDiagnoserTests of ARROW_Framework.</summary>
public class FailureDiagnoserTests
{
    private const string TestPath = "shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java";

    private static readonly RepositoryMetadata M = new(
        new SourceMetadata(ProjectSourceKind.LocalDirectory, "MyShop", null, null, null),
        new BuildToolMetadata(BuildToolKind.Maven, "3.9.6", false, false, ".", BuildMode.Reactor, "pom.xml", "shop-core", "detected:reactor", [], []),
        null,
        null,
        new TestFrameworkMetadata("junit4", "4.13.2", false, [], [], "dependency junit:junit (test)"),
        new ProjectStructureMetadata(true, ["shop-core"], "shop-core", "shop-core", ["com.acme.shop"]),
        new PathsMetadata("shop-core/src/main/java", "shop-core/src/test/java", "shop-core/src/main/java/com/acme/shop/OrderService.java",
            TestPath, "com.acme.shop.OrderService_ArrowTest", 1, true),
        new CutMetadata("com.acme.shop.OrderService", "OrderService", "com.acme.shop", CutKind.Class, "abc"),
        new Dictionary<string, string>());

    private static CandidateTest Candidate(string code = "") =>
        new(true, null, null, code, "com.acme.shop.OrderService_ArrowTest", TestPath, "sha256:from-block2", 1, 1, [], [], "stop");

    private readonly FailureDiagnoser _diagnoser = new();

    // Parity: FailureDiagnoserTests.CompileBlockShowsErrorsSymbolsAndCode
    [Fact]
    public void CompileBlockShowsErrorsSymbolsAndCode()
    {
        const string code = "package com.acme.shop;\nclass OrderService_ArrowTest {\n  @Test void totalPriceAddsTax() {\n    long t = service.getTotal(1L);\n  }\n}\n";
        var result = new VerifyResult
        {
            State = VerificationState.Compile,
            ErrorSignature = "COMPILE | cannot find symbol | method",
            FailingMethods = ["totalPriceAddsTax"],
            CodeHash = "sha256:verify",
            CompileErrors =
            [
                new CompileError(TestPath, 4, 22, "cannot find symbol", "cannot find symbol", "method", "getTotal(long)", "variable service of type com.acme.shop.OrderService", InGeneratedFile: true),
                new CompileError("shop-core/src/main/java/com/acme/shop/Other.java", 9, null, "cannot find symbol", "cannot find symbol"),
            ],
        };
        var diagnosis = _diagnoser.Diagnose(result, Candidate(code), M);
        Assert.Equal((VerificationState.Compile, ErrorGroup.Build), (diagnosis.State, diagnosis.Group!.Value));
        Assert.Equal("COMPILE | cannot find symbol | method", diagnosis.ErrorSignature);
        Assert.Equal("sha256:from-block2", diagnosis.CodeHash);
        Assert.Equal(["totalPriceAddsTax"], diagnosis.FailingMethods);
        Assert.Equal("""
            shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java:4:22: error: cannot find symbol
              symbol: method getTotal(long)
              location: variable service of type com.acme.shop.OrderService
              code: long t = service.getTotal(1L);
            """.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(), diagnosis.ErrorBlock);
        Assert.Equal("cannot find symbol: method getTotal(long) in variable service of type com.acme.shop.OrderService", diagnosis.FirstError);
    }

    [Fact]
    public void CompileBlockWithoutAnErrorInTheGeneratedFileShowsEveryError()
    {
        var diagnosis = _diagnoser.Diagnose(new VerifyResult
        {
            State = VerificationState.Compile,
            CompileErrors = [new CompileError("src/test/java/Helper.java", 7, null, "package org.junit.jupiter.api does not exist", "package does not exist")],
        }, Candidate(), M);
        Assert.Equal("src/test/java/Helper.java:7: error: package org.junit.jupiter.api does not exist", diagnosis.ErrorBlock);
        Assert.Equal("package org.junit.jupiter.api does not exist", diagnosis.FirstError);
    }

    // Parity: FailureDiagnoserTests.UnknownAndDiscoveryBlocks
    [Fact]
    public void UnknownAndDiscoveryBlocks()
    {
        var unknown = _diagnoser.Diagnose(new VerifyResult
        {
            State = VerificationState.Unknown,
            ErrorSignature = "UNKNOWN | maven-enforcer-plugin | -",
            BuildErrorExcerpt = "[ERROR] Failed to execute goal org.apache.maven.plugins:maven-enforcer-plugin:3.0.0:enforce\n[ERROR] Rule 0 failed",
            FailingMethods = ["ignored"],
        }, Candidate(), M);
        Assert.Equal(ErrorGroup.Build, unknown.Group);
        Assert.Empty(unknown.FailingMethods);
        Assert.StartsWith("[ERROR] Failed to execute goal", unknown.ErrorBlock, StringComparison.Ordinal);
        Assert.Equal("Failed to execute goal org.apache.maven.plugins:maven-enforcer-plugin:3.0.0:enforce", unknown.FirstError);

        var discovery = _diagnoser.Diagnose(new VerifyResult { State = VerificationState.Discovery, ErrorSignature = "DISCOVERY | no tests executed | -", TestFile = TestPath }, Candidate(), M);
        Assert.Equal("The test runner executed 0 test methods of com.acme.shop.OrderService_ArrowTest (file shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java). The project runs tests with JUnit 4.", discovery.ErrorBlock);
        Assert.Equal("no tests executed", discovery.FirstError);
    }

    // Parity: FailureDiagnoserTests.RuntimeAndAssertionBlocksListFailingMethods
    [Fact]
    public void RuntimeAndAssertionBlocksListFailingMethods()
    {
        var failures = Enumerable.Range(1, 7).Select(i => new FailureInfo(
            $"test{i}",
            "java.lang.NullPointerException",
            "Cannot invoke \"TaxPolicy.apply(long)\" because \"this.tax\" is null\nsecond line",
            "java.lang.NullPointerException: x\n\tat com.acme.shop.OrderService.totalPrice(OrderService.java:31)\n\tat com.acme.shop.OrderService_ArrowTest.test1(OrderService_ArrowTest.java:14)")).ToList();
        var runtime = _diagnoser.Diagnose(new VerifyResult
        {
            State = VerificationState.Runtime,
            Failures = failures,
            FailingMethods = failures.Select(f => f.Method).ToList(),
            ErrorSignature = "RUNTIME | NullPointerException | OrderService.totalPrice",
        }, Candidate(), M);
        Assert.Equal(ErrorGroup.Runtime, runtime.Group);
        Assert.Equal(7, runtime.FailingMethods.Count);
        var lines = runtime.ErrorBlock.Split('\n');
        Assert.Equal("Test method: test1", lines[0]);
        Assert.Equal("Exception: java.lang.NullPointerException: Cannot invoke \"TaxPolicy.apply(long)\" because \"this.tax\" is null", lines[1]);
        Assert.Equal("  at com.acme.shop.OrderService.totalPrice(OrderService.java:31)", lines[2]);
        Assert.Equal("test1: NullPointerException", runtime.FirstError);
        Assert.Equal(5, lines.Count(l => l.StartsWith("Test method:", StringComparison.Ordinal)));
        Assert.Equal("… and 2 more failing test methods", lines[^1]);

        var withFrame = _diagnoser.Diagnose(new VerifyResult { State = VerificationState.Runtime, Failures = [failures[0] with { Method = "totalPriceAddsTax", Frame = "OrderService.totalPrice" }] }, Candidate(), M);
        Assert.Equal("totalPriceAddsTax: NullPointerException at OrderService.totalPrice", withFrame.FirstError);

        var assertion = _diagnoser.Diagnose(new VerifyResult
        {
            State = VerificationState.Assertion,
            Failures = [new FailureInfo("totalPriceAddsTax", "java.lang.AssertionError", "expected:<110> but was:<100>")],
            FailingMethods = ["totalPriceAddsTax"],
        }, Candidate(), M);
        Assert.Equal(ErrorGroup.Assertion, assertion.Group);
        Assert.Equal("Test method: totalPriceAddsTax\nException: java.lang.AssertionError: expected:<110> but was:<100>", assertion.ErrorBlock);
        Assert.Equal("totalPriceAddsTax: expected:<110> but was:<100>", assertion.FirstError);
    }

    [Fact]
    public void RuntimeWithoutReportShowsTheBuildError()
    {
        var crash = _diagnoser.Diagnose(new VerifyResult
        {
            State = VerificationState.Runtime,
            ErrorSignature = "RUNTIME | maven-surefire-plugin | -",
            BuildErrorExcerpt = "[ERROR] Failed to execute goal org.apache.maven.plugins:maven-surefire-plugin:3.2.5:test (default-test) on project shop: The forked VM terminated without properly saying goodbye.",
        }, Candidate(), M);
        Assert.StartsWith("[ERROR] Failed to execute goal", crash.ErrorBlock, StringComparison.Ordinal);
        Assert.StartsWith("Failed to execute goal org.apache.maven.plugins:maven-surefire-plugin", crash.FirstError, StringComparison.Ordinal);
        Assert.Empty(crash.FailingMethods);
    }

    // Parity: FailureDiagnoserTests.RegressionBlockAndLimits. P04 §4.2 adds the line for case (c), so the text differs.
    [Fact]
    public void RegressionBlockAndLimits()
    {
        var regression = _diagnoser.Diagnose(new VerifyResult
        {
            State = VerificationState.Regression,
            ErrorSignature = "REGRESSION | existing tests fail | CartTest,InvoiceTest,OrderService_ArrowTest",
            Regressions =
            [
                new RegressionInfo("com.acme.InvoiceTest#appliesDefaultTax", RegressionKind.Failed, "java.lang.AssertionError", "expected:<110> but was:<120>"),
                new RegressionInfo("com.acme.CartTest#adds", RegressionKind.Missing),
                new RegressionInfo("com.acme.shop.OrderService_ArrowTest#addsTax", RegressionKind.GeneratedFailedInModule, "java.lang.AssertionError", "expected:<1> but was:<2>"),
            ],
        }, Candidate(), M);
        Assert.Equal(ErrorGroup.Runtime, regression.Group);
        Assert.Equal("""
            The generated test passes alone, but running the whole module together with it breaks these existing tests:
            - com.acme.InvoiceTest#appliesDefaultTax: java.lang.AssertionError: expected:<110> but was:<120>
            - com.acme.CartTest#adds: was not run (missing from the report)
            The generated test failed when run with the module:
            - com.acme.shop.OrderService_ArrowTest#addsTax: java.lang.AssertionError: expected:<1> but was:<2>
            """.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(), regression.ErrorBlock);
        Assert.Equal("com.acme.InvoiceTest#appliesDefaultTax: AssertionError", regression.FirstError);
        Assert.Empty(regression.FailingMethods);

        var onlyOwn = _diagnoser.Diagnose(new VerifyResult
        {
            State = VerificationState.Regression,
            Regressions = [new RegressionInfo("com.acme.shop.OrderService_ArrowTest#addsTax", RegressionKind.GeneratedFailedInModule, "java.lang.IllegalStateException")],
        }, Candidate(), M);
        Assert.StartsWith("The generated test passes alone, but it failed when run with the module:\n", onlyOwn.ErrorBlock, StringComparison.Ordinal);
        Assert.Equal("com.acme.shop.OrderService_ArrowTest#addsTax: IllegalStateException", onlyOwn.FirstError);

        var valid = _diagnoser.Diagnose(new VerifyResult { State = VerificationState.Valid }, Candidate(), M);
        Assert.Equal(("", (ErrorGroup?)null), (valid.ErrorBlock, valid.Group));
    }

    [Fact]
    public void InfraHasNoDiagnosis() =>
        Assert.Throws<ArgumentException>(() => _diagnoser.Diagnose(new VerifyResult { InfraReason = "B3_TIMEOUT" }, Candidate(), M));

    // P04 §12 item 9: at most 60 lines, at most 200 characters.
    [Fact]
    public void BlocksAndFirstErrorsAreLimited()
    {
        var long70 = string.Join('\n', Enumerable.Range(1, 70).Select(i => $"line {i}"));
        var limited = FailureDiagnoser.Limit(long70);
        Assert.Equal(60, limited.Split('\n').Length);
        Assert.EndsWith("line 59\n… (11 more lines)", limited, StringComparison.Ordinal);
        Assert.Equal(string.Join('\n', Enumerable.Range(1, 60).Select(i => $"line {i}")), FailureDiagnoser.Limit(string.Join('\n', Enumerable.Range(1, 60).Select(i => $"line {i}")) + "\n"));

        var failures = Enumerable.Range(1, 5).Select(i => new FailureInfo(
            $"test{i}", "java.lang.AssertionError", new string('x', 500),
            string.Join('\n', Enumerable.Range(1, 8).Select(j => $"\tat com.acme.shop.Deep.level{j}(Deep.java:{j})")))).ToList();
        var assertion = _diagnoser.Diagnose(new VerifyResult { State = VerificationState.Assertion, Failures = failures }, Candidate(), M);
        Assert.True(assertion.ErrorBlock.Split('\n').Length <= FailureDiagnoser.MaxBlockLines);
        Assert.Equal(200, assertion.FirstError!.Length);
        Assert.EndsWith("…", assertion.FirstError, StringComparison.Ordinal);
    }

    // P04 §4.2: the block keeps only paths from the project root, because the verifier made them relative.
    [Fact]
    public void CompileBlockFromAWorkspaceLogHasNoAbsolutePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "arrow-ws", "copy-1");
        var file = Path.Combine(root, "shop-core", "src", "test", "java", "com", "acme", "shop", "OrderService_ArrowTest.java");
        var log = $"[ERROR] {file}:[1,1] cannot find symbol\n  symbol:   class Money\n  location: class com.acme.shop.OrderService_ArrowTest";
        var errors = JavacErrorParser.Parse(log, ["OrderService_ArrowTest.java"], root);
        var diagnosis = _diagnoser.Diagnose(new VerifyResult { State = VerificationState.Compile, CompileErrors = errors }, Candidate("class X {}"), M);
        Assert.DoesNotContain(root, diagnosis.ErrorBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root.Replace('\\', '/'), diagnosis.ErrorBlock, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TestPath + ":1:1: error: cannot find symbol", diagnosis.ErrorBlock, StringComparison.Ordinal);
    }
}
