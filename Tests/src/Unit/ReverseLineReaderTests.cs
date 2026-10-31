namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ReverseLineReaderTests
{
    private async Task<List<(long Offset, string Line)>> Read(byte[] content)
    {
        using MemoryStream stream = new(content);
        return await new ReverseLineReader().Read(stream).ToListAsync();
    }

    private async Task<List<(long Offset, string Line)>> Read(string content)
        => await Read(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Read_ReturnsTheLinesFromTheLastWithTheirOffsets()
        => Assert.Equal([(8L, "three"), (4L, "two"), (0L, "one")], await Read("one\ntwo\nthree\n"));

    [Fact]
    public async Task Read_SeesALastLineWithoutALineBreak()
        => Assert.Equal([(4L, "two"), (0L, "one")], await Read("one\ntwo"));

    [Fact]
    public async Task Read_SkipsEmptyLinesAndTrimsCarriageReturns()
        => Assert.Equal([(8L, "b"), (3L, "a")], await Read("\n\r\na\r\n\n\nb\r\n"));

    [Fact]
    public async Task Read_OfAnEmptyStream_ReturnsNothing()
        => Assert.Empty(await Read(Array.Empty<byte>()));

    [Fact]
    public async Task Read_OfOneLine_ReturnsItAtOffsetZero()
        => Assert.Equal([(0L, "only")], await Read("only"));

    [Fact]
    public async Task Read_AcrossManyChunks_KeepsEveryLineWholeIncludingMultiByteCharacters()
    {
        string[] lines = [.. Enumerable.Range(0, 20000).Select(number => $"line {number} héllo wörld 日本語 {new string('x', number % 37)}")];
        byte[] content = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");

        List<(long Offset, string Line)> read = await Read(content);

        Assert.Equal(lines.Reverse(), read.Select(entry => entry.Line));
        Assert.All(read, entry => Assert.Equal(entry.Line, Encoding.UTF8.GetString(content, (int)entry.Offset, Encoding.UTF8.GetByteCount(entry.Line))));
    }

    [Fact]
    public async Task Read_OfOneLineLongerThanAChunk_ReturnsItWhole()
    {
        string line = new('y', 200_000);

        Assert.Equal([(5L, line), (0L, "head")], await Read($"head\n{line}\n"));
    }

    [Fact]
    public async Task Read_WhenCancelled_Throws()
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("a\nb\n"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReverseLineReader().Read(stream, cancellation.Token).ToListAsync().AsTask());
    }
}
