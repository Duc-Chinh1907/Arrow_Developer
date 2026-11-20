using System.Security.Cryptography;
using Arrow.Business.UseCases;
using Arrow.Common;
using Arrow.Common.Errors;
using Arrow.Common.Generation;
using Arrow.DataAccess.Source;

namespace Arrow.Business.Tests.UseCases;

/// <summary><c>arrow gen --c0</c> (P04 §6.8, Chính 20/11/2026): the checks made before a run exists.</summary>
public sealed class C0FileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arrow-c0-tests", Guid.NewGuid().ToString("N")[..10]);

    public C0FileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static GenerateRequest Request(GenerateOptions options, params string[] classes) =>
        new(ProjectSourceKind.LocalDirectory, "/p", null, classes, null, options);

    private static GenerateOptions Options(string? file = null, string? model = null, string? state = null) =>
        new(null, null, false, true, true, true, null, C0File: file, C0Model: model, C0State: state);

    private static string Message(Action action) => Assert.Throws<ArrowException>(action) is { Code: ErrorCodes.CliArgs } ex ? ex.Message : "wrong code";

    [Fact]
    public void WithoutC0NothingIsRead()
    {
        Assert.Equal((null, null), GenerateTestsUseCase.ReadC0(Request(Options(), "A")));
    }

    [Fact]
    public void TheFileIsReadWithItsHashModelAndState()
    {
        var path = Path.Combine(_dir, "A_ArrowTest.java");
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "package a;\nclass A_ArrowTest {}\n"u8.ToArray()];
        File.WriteAllBytes(path, bytes);

        var (file, code) = GenerateTestsUseCase.ReadC0(Request(Options(path, " gpt6luna ", "compile"), "A"));

        Assert.Equal("package a;\nclass A_ArrowTest {}\n", code);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file!.Sha256);
        Assert.Equal(("gpt6luna", VerificationState.Compile), (file.GenerationModel, file.PreviousState!.Value));
        Assert.Equal(Path.GetFullPath(path).Replace('\\', '/'), file.File);
    }

    [Fact]
    public void ModelAndStateNeedC0()
    {
        Assert.Contains("chỉ dùng cùng --c0", Message(() => GenerateTestsUseCase.ReadC0(Request(Options(model: "gpt6luna"), "A"))), StringComparison.Ordinal);
        Assert.Contains("chỉ dùng cùng --c0", Message(() => GenerateTestsUseCase.ReadC0(Request(Options(state: "COMPILE"), "A"))), StringComparison.Ordinal);
    }

    [Fact]
    public void C0IsForOneClass()
    {
        var path = Path.Combine(_dir, "A.java");
        File.WriteAllText(path, "class A {}");
        Assert.Contains("đúng một class", Message(() => GenerateTestsUseCase.ReadC0(Request(Options(path), "A", "B"))), StringComparison.Ordinal);
        Assert.Contains("đúng một class", Message(() => GenerateTestsUseCase.ReadC0(Request(Options(path)) with { Package = "a" })), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingOrLargeFileOrAnUnknownStateIsRefused()
    {
        Assert.Contains("Không thấy file c0", Message(() => GenerateTestsUseCase.ReadC0(Request(Options(Path.Combine(_dir, "none.java")), "A"))), StringComparison.Ordinal);

        var large = Path.Combine(_dir, "Large.java");
        File.WriteAllBytes(large, new byte[C0File.MaxBytes + 1]);
        Assert.Contains("lớn hơn", Message(() => GenerateTestsUseCase.ReadC0(Request(Options(large), "A"))), StringComparison.Ordinal);

        var path = Path.Combine(_dir, "A.java");
        File.WriteAllText(path, "class A {}");
        Assert.Contains("COMPILE, UNKNOWN, DISCOVERY, RUNTIME, ASSERTION, REGRESSION, VALID",
            Message(() => GenerateTestsUseCase.ReadC0(Request(Options(path, state: "INVALID_OUTPUT"), "A"))), StringComparison.Ordinal);
    }

    [Fact]
    public void SameModelIsUnknownWithoutAGenerationModel()
    {
        Assert.Equal((true, false, (bool?)null), (C0File.SameModel("gpt6luna", "gpt6luna"), C0File.SameModel("qwen3coder-or", "gpt6luna"), C0File.SameModel(null, "gpt6luna")));
    }
}
