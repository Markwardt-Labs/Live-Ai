namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptImageToolTests
{
    private readonly IStore store = new MemoryStore();

    private readonly List<(string Path, PromptAttachment Image)> given = [];

    private PromptTool Tool()
        => new PromptLibraryTools().CreateImage(store, (path, image, _) =>
        {
            given.Add((path, image));
            return Task.CompletedTask;
        });

    private async Task<string> Run(string json)
        => await Tool().Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);

    private async Task Put(string path, int length)
    {
        await using Stream stream = await store.Open(path, StoreOpenMode.Replace);
        await stream.WriteAsync(Enumerable.Range(0, length).Select(index => (byte)index).ToArray());
    }

    [Fact]
    public void Create_IsNamedAfterTheReservedNameAndTakesAPath()
    {
        PromptTool tool = Tool();

        Assert.Equal("view_image", tool.Name);
        Assert.Equal("view_image", new PromptLibraryTools().ImageName);
        Assert.Contains("path", tool.InputSchema.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_SaysItsPathsAreTheOnesOfTheFileTools()
    {
        string description = Tool().Description;

        Assert.Contains("same paths as the file tools", description, StringComparison.Ordinal);
        Assert.Contains(".liveai-session", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs/shot.PNG", "image/png")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    public async Task Run_GivesTheImageOfTheStoreByItsPathAndTellsTheAiItIsOnItsWay(string path, string type)
    {
        await Put(path, 4);

        string told = await Run($$"""{"path":"{{path}}"}""");

        Assert.Equal(path, given.Single().Path);
        Assert.Equal((Path.GetFileName(path), type), (given.Single().Image.Name, given.Single().Image.MediaType));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, given.Single().Image.Content.ToArray());
        Assert.Contains("on its way", told, StringComparison.Ordinal);
        Assert.Contains("call view_image again", told, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_CleansThePathTheWayTheFileToolsDo()
    {
        await Put("docs/a.png", 1);

        await Run("""{"path":"docs\\./x/../a.png"}""");

        Assert.Equal("docs/a.png", given.Single().Path);
    }

    [Theory]
    [InlineData("""{"path":""}""", "Give the path")]
    [InlineData("""{}""", "Give the path")]
    [InlineData("""{"path":"notes.txt"}""", "Only PNG")]
    public async Task Run_RefusesWhatItCannotShow(string json, string reason)
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(json));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.Empty(given);
    }

    [Fact]
    public async Task Run_PassesOnAFileThatIsMissingAndRefusesAnImageThatIsTooLarge()
    {
        await Put("big.png", 5 * 1024 * 1024 + 1);

        await Assert.ThrowsAnyAsync<IOException>(() => Run("""{"path":"missing.png"}"""));
        IOException large = await Assert.ThrowsAsync<IOException>(() => Run("""{"path":"big.png"}"""));

        Assert.Contains("larger than 5 MB", large.Message, StringComparison.Ordinal);
        Assert.Empty(given);
    }
}
