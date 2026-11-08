namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptDownloadToolTests
{
    private readonly MemoryStore store = new();

    private Func<HttpRequestMessage, HttpResponseMessage> answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private long limit = 1000;

    private async Task<string> Run(string json)
    {
        PromptTool tool = new PromptLibraryTools(new Answering(request => answer(request))).CreateDownload(store, () => limit);
        return await tool.Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);
    }

    private HttpResponseMessage File(string type, byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(type) } } };

    [Fact]
    public void Create_IsNamedAfterTheReservedNameAndTellsTheLimit()
    {
        PromptTool tool = new PromptLibraryTools().CreateDownload(store, () => 5 * 1024 * 1024);

        Assert.Equal("download_url", tool.Name);
        Assert.Equal("download_url", new PromptLibraryTools().DownloadName);
        Assert.Contains("5 MiB", tool.Description);
        Assert.Contains(".liveai-cache/downloads", tool.Description);
    }

    [Fact]
    public async Task Run_WithoutAPath_SavesInTheDownloadsFolderOfTheCacheUnderTheNameInTheAddress()
    {
        answer = _ => File("application/octet-stream", [1, 2, 3]);

        string result = await Run("""{"url":"https://example.com/files/data.bin"}""");

        Assert.Contains(".liveai-cache/downloads/data.bin", result);
        await using Stream stream = await store.Open(".liveai-cache/downloads/data.bin", StoreOpenMode.Read);
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
    }

    [Fact]
    public async Task Run_WithoutAPath_NumbersTheNameInsteadOfReplacingAFile()
    {
        answer = _ => File("image/png", [1]);

        await Run("""{"url":"https://example.com/pic.png"}""");
        string second = await Run("""{"url":"https://example.com/pic.png"}""");

        Assert.Contains(".liveai-cache/downloads/pic-2.png", second);
        Assert.Equal(1, await store.GetSize(".liveai-cache/downloads/pic.png"));
    }

    [Fact]
    public async Task Run_OfAnAddressWithoutAFileName_NamesTheFileFromTheMediaType()
    {
        answer = _ => File("image/jpeg", [1]);

        string result = await Run("""{"url":"https://example.com/"}""");

        Assert.Contains(".liveai-cache/downloads/download.jpg", result);
        Assert.Contains("Look at it with view_image", result);
    }

    [Fact]
    public async Task Run_WithAPath_SavesThereAndNeverReplacesAFile()
    {
        answer = _ => File("text/plain", [65]);
        await Run("""{"url":"https://example.com/a","path":"out/here.txt"}""");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/a","path":"out/here.txt"}"""));

        Assert.Contains("already exists", failure.Message);
        Assert.Equal(1, await store.GetSize("out/here.txt"));
    }

    [Fact]
    public async Task Run_WithAPathThatEndsInASlash_SavesInThatFolderUnderTheNameInTheAddress()
    {
        answer = _ => File("application/pdf", [1]);

        string result = await Run("""{"url":"https://example.com/report","path":"docs/"}""");

        Assert.Contains("docs/report.pdf", result);
    }

    [Fact]
    public async Task Run_OfAFileLargerThanTheLimit_SavesNothing()
    {
        answer = _ => File("application/octet-stream", new byte[1001]);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/big.bin"}"""));

        Assert.Contains("a download may have", failure.Message);
        Assert.Empty(await store.List(string.Empty, true).Where(entry => !entry.IsDirectory).ToListAsync());
    }

    [Fact]
    public async Task Run_OfAFileThatPassesTheLimitWhileItStreams_RemovesWhatWasWritten()
    {
        answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[5000])) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/big.bin","path":"big.bin"}"""));

        Assert.Empty(await store.List(string.Empty, true).Where(entry => !entry.IsDirectory).ToListAsync());
    }

    [Theory]
    [InlineData("ftp://example.com/a")]
    [InlineData("not an address")]
    public async Task Run_OfAnAddressThatIsNotHttp_Refuses(string url)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Run($$"""{"url":"{{url}}"}"""));

    [Fact]
    public async Task Run_WhenTheServerAnswersWithAnError_SaysSo()
    {
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/missing"}"""));

        Assert.Contains("404", failure.Message);
    }

    [Fact]
    public async Task Run_NamesTheFileSafelyFromWhatTheAddressSays()
    {
        answer = _ => File("text/plain", [1]);

        string result = await Run("""{"url":"https://example.com/a%3Ab%2F..%2Fc.txt"}""");

        Assert.StartsWith("Saved .liveai-cache/downloads/ab..c.txt (", result);
    }
}
