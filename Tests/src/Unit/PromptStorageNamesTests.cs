namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptStorageNamesTests
{
    [Fact]
    public void TheNames_AreTheOnesTheLibraryToolsUse()
    {
        IPromptLibraryTools tools = new PromptLibraryTools();

        Assert.Equal(".liveai-session", PromptStorageNames.Session);
        Assert.Equal(".liveai-cache", PromptStorageNames.Cache);
        Assert.Equal(".liveai-temp", PromptStorageNames.Temp);
        Assert.Equal(PromptStorageNames.Session, tools.SessionName);
        Assert.Equal(PromptStorageNames.Cache, tools.CacheName);
        Assert.Equal(PromptStorageNames.Temp, tools.WorkspaceTempName);
    }

    [Fact]
    public async Task TheFilesOfAFakeSession_HaveThePlacesAtTheseNames()
    {
        await using FakePromptSession session = new();

        await session.Workspace.WriteText($"{PromptStorageNames.Session}/a.txt", "one");
        await session.Workspace.WriteText($"{PromptStorageNames.Cache}/b.txt", "two");

        Assert.Equal("one", await session.Workspace.ReadText(".liveai-session/a.txt"));
        Assert.Equal("two", await session.Workspace.ReadText(".liveai-cache/b.txt"));
    }
}
