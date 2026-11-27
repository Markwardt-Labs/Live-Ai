namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptStorageNamesTests
{
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
