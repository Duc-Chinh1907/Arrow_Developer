using Arrow.Common;
using Arrow.Common.Events;
using Arrow.DataAccess.IO;

namespace Arrow.DataAccess.Tests.IO;

/// <summary>Atomic writes (P00 §5) and JSONL files.</summary>
public class FileTests
{
    /// <summary>Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/FileHelpersTests.AtomicWriteOverwritesAndLeavesNoTempFiles</c>.</summary>
    [Fact]
    public void AtomicWriteOverwritesAndLeavesNoTempFiles()
    {
        using var temp = new TempDir();
        var path = temp.PathOf("a", "b.json");
        AtomicFile.WriteAllText(path, "one\r\n");
        AtomicFile.WriteAllText(path, "two\r\n");
        Assert.Equal("two\n", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    /// <summary>Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/FileHelpersTests.ConcurrentAtomicWritesToSameFileDoNotFail</c>.</summary>
    [Fact]
    public async Task ConcurrentAtomicWritesToSameFileDoNotFail()
    {
        using var temp = new TempDir();
        var path = temp.PathOf("status.json");
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => AtomicFile.WriteAllText(path, $"value {i}"), TestContext.Current.CancellationToken)));
        Assert.StartsWith("value ", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(temp.Root, "status.json*"));
    }

    [Fact]
    public void AtomicTextIsUtf8WithoutBom()
    {
        using var temp = new TempDir();
        var path = temp.PathOf("vi.txt");
        AtomicFile.WriteAllText(path, "Không biên dịch được");
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("Không biên dịch được", File.ReadAllText(path));
    }

    /// <summary>
    /// Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/FileHelpersTests.JsonlAppendFromManyThreadsKeepsEveryLine</c>,
    /// with <c>HistoryEntry</c> in place of the research ledger entry.
    /// </summary>
    [Fact]
    public async Task JsonlAppendFromManyThreadsKeepsEveryLine()
    {
        using var temp = new TempDir();
        var path = temp.PathOf("history.jsonl");
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(
            () => JsonlFile.Append(path, new HistoryEntry($"r{i}", DateTimeOffset.UnixEpoch, "p", [], 0, 0, RunStatus.Completed)),
            TestContext.Current.CancellationToken)));
        var entries = JsonlFile.ReadAll<HistoryEntry>(path);
        Assert.Equal(200, entries.Items.Count);
        Assert.Equal(200, entries.Items.Select(e => e.RunId).Distinct().Count());
        Assert.Equal(0, entries.SkippedLines);
    }

    /// <summary>Parity: <c>ARROW_Framework/tests/Arrow.DataAccess.Tests/FileHelpersTests.JsonlIgnoresHalfWrittenLastLine</c>.</summary>
    [Fact]
    public void JsonlIgnoresHalfWrittenLastLine()
    {
        using var temp = new TempDir();
        var path = temp.PathOf("events.jsonl");
        JsonlFile.Append(path, Event("a"));
        File.AppendAllText(path, "{\"message\": \"b");
        var content = JsonlFile.ReadAll<ArrowEvent>(path);
        Assert.Equal("a", Assert.Single(content.Items).Message);
        Assert.Equal(1, content.SkippedLines);
    }

    [Fact]
    public void JsonlSkipsDamagedLinesAnywhereAndAppliesTheTransform()
    {
        using var temp = new TempDir();
        var path = temp.PathOf("events.jsonl");
        JsonlFile.Append(path, Event("first"));
        File.AppendAllText(path, "not json\n");
        JsonlFile.Append(path, Event("secret"), line => line.Replace("secret", "***", StringComparison.Ordinal));
        var content = JsonlFile.ReadAll<ArrowEvent>(path);
        Assert.Equal(["first", "***"], content.Items.Select(e => e.Message));
        Assert.Equal(1, content.SkippedLines);
        Assert.Empty(JsonlFile.ReadAll<ArrowEvent>(temp.PathOf("missing.jsonl")).Items);
    }

    private static ArrowEvent Event(string message) =>
        new(DateTimeOffset.UnixEpoch, ArrowEventTypes.RunStarted, null, null, null, EventLevel.Info, message, new Dictionary<string, object?>());
}
