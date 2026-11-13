using Arrow.Business.Generation;

namespace Arrow.Business.Tests.Generation;

/// <summary>
/// Step 5.1 and the pure part of 5.3 of P02 (§9). Parity tests rewritten from <c>Block2Tests</c> of ARROW_Framework:
/// <c>ExtractsPlainCodeWithoutChanges</c>, <c>RemovesFenceAndProse</c>, <c>PicksFirstFenceWithClassAndHandlesMissingCode</c>,
/// <c>RemovesDuplicateImports</c>; the inputs and expected values are the old ones.
/// </summary>
public class CodeExtractorTests
{
    // Parity: ExtractsPlainCodeWithoutChanges
    [Fact]
    public void PlainCodeIsKeptAsItIs()
    {
        const string code = "package a;\n\nimport org.junit.Test;\n\npublic class X_ArrowTest {\n    @Test public void t() {}\n}\n";
        var extracted = CodeExtractor.Extract(code);
        Assert.Equal(code, extracted.Code);
        Assert.Empty(extracted.Normalization);
    }

    // Parity: RemovesFenceAndProse
    [Fact]
    public void RemovesTheFenceAndTheProse()
    {
        const string response = "Here is the test class you asked for:\n\n```java\nimport org.junit.Test;\n\npublic class X_ArrowTest {\n    @Test public void t() {}\n}\n```\n\nThis covers the main cases.";
        var extracted = CodeExtractor.Extract(response);
        Assert.Equal("import org.junit.Test;\n\npublic class X_ArrowTest {\n    @Test public void t() {}\n}\n", extracted.Code);
        Assert.Equal([CodeExtractor.FenceRemoved], extracted.Normalization);

        var noFence = CodeExtractor.Extract("Sure!\n@RunWith(JUnit4.class)\npublic class X { }\nHope it helps.");
        Assert.Equal("@RunWith(JUnit4.class)\npublic class X { }\n", noFence.Code);
        Assert.Equal([CodeExtractor.ProseTrimmed], noFence.Normalization);
    }

    // The example of P02 §10: a fence, prose around it, a wrong class name (the rename is step 5.3, by java-helper).
    [Fact]
    public void TheExampleOfTheSpecGivesFenceRemovedAndProseTrimmed()
    {
        const string answer = "Here is the test:\n```java\npackage com.myshop.service;\nimport org.junit.jupiter.api.Test;\nimport static org.junit.jupiter.api.Assertions.*;\nclass OrderServiceTest {\n  @Test void totalIsZeroForEmptyOrder() { assertEquals(0, new OrderService(new InMemoryRepo()).totalPrice(1L)); }\n}\n```\nHope this helps.";
        var extracted = CodeExtractor.Extract(answer);
        Assert.StartsWith("package com.myshop.service;", extracted.Code, StringComparison.Ordinal);
        Assert.EndsWith("}\n", extracted.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("Hope this helps", extracted.Code, StringComparison.Ordinal);
        Assert.Equal([CodeExtractor.FenceRemoved], extracted.Normalization);

        // Prose inside the fence's neighbours but outside the fence block counts as the fence being removed only; prose around no fence is trimmed.
        var prose = CodeExtractor.Extract("Intro\nclass A {}\nOutro");
        Assert.Equal("class A {}\n", prose.Code);
        Assert.Equal([CodeExtractor.ProseTrimmed], prose.Normalization);
    }

    // Parity: PicksFirstFenceWithClassAndHandlesMissingCode
    [Fact]
    public void TakesTheFirstFenceThatDeclaresATypeAndHandlesMissingCode()
    {
        Assert.Equal("class A {}\n", CodeExtractor.Extract("```xml\n<dependency/>\n```\n```java\nclass A {}\n```").Code);
        Assert.Equal("class A {}\n", CodeExtractor.Extract("```\nclass A {}\n```").Code);
        Assert.Null(CodeExtractor.Extract("I cannot write a test for this class.").Code);
        Assert.Null(CodeExtractor.Extract("").Code);
        Assert.Null(CodeExtractor.Extract(null).Code);
        Assert.Null(CodeExtractor.Extract("```java\n// nothing here\n```").Code);

        // An unclosed fence (the answer was cut): the code is kept so that the parser reports the error.
        var truncated = CodeExtractor.Extract("```java\npublic class A {\n  @Test public void t() {\n    int x =");
        Assert.NotNull(truncated.Code);
        Assert.Contains("int x =", truncated.Code, StringComparison.Ordinal);
        Assert.Contains(CodeExtractor.FenceRemoved, truncated.Normalization);
    }

    [Theory]
    [InlineData("public final class A {}", "public final class A {}\n")]
    [InlineData("interface I { void f(); }", "interface I { void f(); }\n")]
    [InlineData("public enum E { A, B }", "public enum E { A, B }\n")]
    [InlineData("record R(int x) {}", "record R(int x) {}\n")]
    public void AnyTypeDeclarationCounts(string answer, string expected) => Assert.Equal(expected, CodeExtractor.Extract(answer).Code);

    [Fact]
    public void CrLfAndTrailingTextOnTheClosingLine()
    {
        var extracted = CodeExtractor.Extract("class A {\r\n}\r\nThanks!");
        Assert.Equal("class A {\n}\n", extracted.Code);
        Assert.Equal([CodeExtractor.ProseTrimmed], extracted.Normalization);

        Assert.Equal("class A { }\n", CodeExtractor.Extract("class A { } // done").Code);
    }

    // Parity: RemovesDuplicateImports
    [Fact]
    public void RemovesRepeatedImportLines()
    {
        var (code, actions) = CodeExtractor.RemoveDuplicateImports("import a.B;\nimport a.C;\nimport a.B;\nclass X {}");
        Assert.Equal("import a.B;\nimport a.C;\nclass X {}", code);
        Assert.Equal(["import: removed duplicate a.B"], actions);

        // Different spacing is the same import; a static import of the same name is not.
        var (spaced, _) = CodeExtractor.RemoveDuplicateImports("import  a.B ;\nimport a.B ;\nimport static a.B.x;\nclass X {}");
        Assert.Equal("import  a.B ;\nimport static a.B.x;\nclass X {}", spaced);
    }

    [Fact]
    public void RemovesARepeatedPackageLine()
    {
        var (code, actions) = CodeExtractor.RemoveDuplicatePackages("package a;\npackage a;\nimport b.C;\nclass X {}");
        Assert.Equal("package a;\nimport b.C;\nclass X {}", code);
        Assert.Equal(["package: removed duplicate"], actions);
        Assert.Empty(CodeExtractor.RemoveDuplicatePackages("package a;\nclass X {}").Actions);
    }
}
