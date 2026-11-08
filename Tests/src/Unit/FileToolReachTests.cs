namespace Markwardt.LiveAi.Tests.Unit;

public sealed class FileToolReachTests
{
    private readonly PromptLibraryTools library = new();

    private readonly MemoryStore cache = new();

    private readonly MemoryStore workspace = new();

    private readonly List<string> shown = [];

    private async Task<IReadOnlyList<PromptTool>> Tools(bool hasWorkspace)
    {
        await cache.WriteText("in-cache.png", "x");
        await workspace.WriteText("in-workspace.png", "x");
        MountedStore store = new(hasWorkspace ? workspace : null, new Dictionary<string, IStore> { [library.SessionName] = cache });
        return [.. library.Create(store), library.CreateImage(store, (path, _, _) =>
        {
            shown.Add(path);
            return Task.CompletedTask;
        })];
    }

    private async Task<string> Run(IReadOnlyList<PromptTool> tools, string name, string path)
        => await tools.Single(tool => tool.Name == name).Handler(JsonDocument.Parse($$"""{"path":"{{path}}"}""").RootElement, CancellationToken.None);

    [Theory]
    [InlineData("read_file")]
    [InlineData("view_image")]
    public async Task TheCacheIsAlwaysReachableWithOrWithoutAWorkspace(string tool)
    {
        foreach (bool hasWorkspace in new[] { true, false })
        {
            shown.Clear();
            IReadOnlyList<PromptTool> tools = await Tools(hasWorkspace);

            string answer = await Run(tools, tool, ".liveai-session/in-cache.png");

            Assert.False(string.IsNullOrEmpty(answer));
            Assert.Equal(tool == "view_image" ? [".liveai-session/in-cache.png"] : [], shown);
        }
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("view_image")]
    public async Task TheWorkspaceIsReachableOnlyWhenThereIsOne(string tool)
    {
        IReadOnlyList<PromptTool> with = await Tools(true);
        await Run(with, tool, "in-workspace.png");

        IReadOnlyList<PromptTool> without = await Tools(false);

        await Assert.ThrowsAnyAsync<Exception>(() => Run(without, tool, "in-workspace.png"));
    }
}
