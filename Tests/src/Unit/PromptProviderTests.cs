namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptProviderTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private readonly Mock<IPromptSession> session = new();

    private readonly List<string> folders = [];

    public PromptProviderTests()
    {
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.HasSession("abc")).Returns(true);
    }

    private PromptProvider Subject()
    {
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>())).Returns(session.Object);
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((record, _) => folders.Add(record.WorkingFolder)).Returns(Task.CompletedTask);
        return new PromptProvider(engine.Object, store.Object, opener.Object, runner.Object);
    }

    private PromptSessionLink Stored()
    {
        PromptSessionLink record = new() { Id = Guid.NewGuid(), Provider = "Fake", WorkingFolder = AppContext.BaseDirectory, AiSessionId = "abc" };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        return record;
    }

    [Fact]
    public async Task List_DescribesTheStoredSessionsOfItsOwnEngineOnly()
    {
        Guid mine = Guid.NewGuid();
        DateTimeOffset created = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        store.Setup(mock => mock.List(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PromptSessionLink { Id = mine, Created = created, Provider = "fake", WorkingFolder = "/w", WorkspacePath = "/project", AiSessionId = "abc" },
            new PromptSessionLink { Id = Guid.NewGuid(), Provider = "Other", WorkingFolder = "/w" },
            new PromptSessionLink { Id = Guid.NewGuid(), Provider = "Fake", WorkingFolder = "/w" }
        ]);

        IReadOnlyList<PromptSessionInfo> sessions = await Subject().List();

        Assert.Equal(2, sessions.Count);
        Assert.Equal(new PromptSessionInfo { Id = mine.ToString("N"), Created = created, WorkspacePath = "/project", IsStarted = true }, sessions[0]);
        Assert.False(sessions[1].IsStarted);
    }

    [Fact]
    public async Task Create_RecordsWhenTheSessionWasMadeOnTheClockOfTheProvider()
    {
        ManualTime clock = new();
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>())).Returns(session.Object);
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((record, _) => folders.Add(record.WorkingFolder)).Returns(Task.CompletedTask);

        await new PromptProvider(engine.Object, store.Object, opener.Object, runner.Object, clock).Create(TestModels.Options);

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Created == clock.GetUtcNow()), It.IsAny<CancellationToken>()), Times.Once);
        Directory.Delete(folders.Single());
    }

    [Fact]
    public async Task Create_StoresTheLinkUnderANewIdentifierAndOpensTheSession()
    {
        IPromptSession created = await Subject().Create(new PromptSessionOptions { Model = TestModels.Model, ApiKey = "key" });

        Assert.Same(session.Object, created);
        Func<PromptSessionLink, bool> expected = record => record.Id != Guid.Empty && record.Provider == "Fake" && record.AiSessionId == null && record.ForkedFrom == null;
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(record => expected(record)), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(record => expected(record)), It.IsAny<IPromptProvider>(), engine.Object, "key", It.IsAny<PromptModel?>()), Times.Once);
        Directory.Delete(folders.Single());
    }

    [Fact]
    public async Task Create_WithAWorkspacePath_StoresItAsTheWorkingFolderAndMakesNoFolder()
    {
        using TempFolder workspace = new();

        await Subject().Create(new PromptSessionOptions { Model = TestModels.Model, WorkspacePath = workspace.Path, Sandbox = false });

        Func<PromptSessionLink, bool> expected = link => link.WorkingFolder == workspace.Path && link.WorkspacePath == workspace.Path && !link.Sandbox;
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => expected(link)), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(link => expected(link)), It.IsAny<IPromptProvider>(), engine.Object, null, It.IsAny<PromptModel?>()), Times.Once);
        Assert.Equal([workspace.Path], folders);
    }

    [Fact]
    public async Task Create_WithAWorkspacePathThatDoesNotExist_ThrowsAndStoresNothing()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Subject().Create(new PromptSessionOptions { Model = TestModels.Model, WorkspacePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}") }));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithoutAWorkspacePath_StoresNoWorkspace()
    {
        await Subject().Create(TestModels.Options);

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.WorkspacePath == null && link.Sandbox), It.IsAny<CancellationToken>()), Times.Once);
        Directory.Delete(folders.Single());
    }

    [Fact]
    public async Task Load_WithAWorkspacePath_IsRefusedBecauseTheWorkspaceIsStoredWithTheSession()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().Load(Guid.NewGuid().ToString("N"), new PromptSessionOptions { Model = TestModels.Model, WorkspacePath = "/somewhere" }));

        store.Verify(mock => mock.Find(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Load_OfAWorkspaceSession_DoesNotMakeTheFolderAgain()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}");
        PromptSessionLink record = Stored() with { WorkingFolder = folder, WorkspacePath = folder, AiSessionId = null };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Load(record.Id.ToString("N"));

        Assert.False(Directory.Exists(folder));
        opener.Verify(mock => mock.Open(record, It.IsAny<IPromptProvider>(), engine.Object, null, It.IsAny<PromptModel?>()), Times.Once);
    }

    [Fact]
    public async Task Delete_NeverRemovesTheWorkspaceFolderEvenWhenItIsNamedLikeAScratchFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        PromptSessionLink record = Stored() with { WorkingFolder = folder, WorkspacePath = folder };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Delete(record.Id.ToString("N"));

        Assert.True(Directory.Exists(folder));
        Directory.Delete(folder);
    }

    [Fact]
    public async Task Create_WhenTheLinkCannotBeStored_LeavesNoScratchFolderBehind()
    {
        string? folder = null;
        PromptProvider subject = Subject();
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((link, _) => folder = link.WorkingFolder).ThrowsAsync(new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => subject.Create(TestModels.Options));

        Assert.NotNull(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Create_GivesEverySessionItsOwnIdentifierAndWorkingFolder()
    {
        List<Guid> ids = [];
        PromptProvider provider = Subject();
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((record, _) =>
        {
            ids.Add(record.Id);
            folders.Add(record.WorkingFolder);
        }).Returns(Task.CompletedTask);

        await provider.Create(TestModels.Options);
        await provider.Create(TestModels.Options);

        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(2, folders.Distinct().Count());
        folders.ForEach(Directory.Delete);
    }

    [Fact]
    public async Task Create_MakesTheFolderTheToolsRunInInTheSystemTempFolderNamedAfterTheSession()
    {
        await Subject().Create(TestModels.Options);

        string folder = folders.Single();
        Assert.True(Directory.Exists(folder));
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(folder));
        Assert.Matches("^markwardt\\.liveai-[0-9a-f]{32}$", Path.GetFileName(folder));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
        Directory.Delete(folder);
    }

    [Fact]
    public async Task Load_OpensTheStoredSessionWithTheApiKey()
    {
        PromptSessionLink record = Stored();

        IPromptSession loaded = await Subject().Load(record.Id.ToString("N"), new PromptSessionOptions { ApiKey = "key" });

        Assert.Same(session.Object, loaded);
        opener.Verify(mock => mock.Open(record, It.IsAny<IPromptProvider>(), engine.Object, "key", It.IsAny<PromptModel?>()), Times.Once);
    }

    [Fact]
    public async Task Load_WhenNothingIsStored_Throws()
    {
        string id = Guid.NewGuid().ToString("N");
        store.Setup(mock => mock.Find(Guid.Parse(id), It.IsAny<CancellationToken>())).ReturnsAsync((PromptSessionLink?)null);

        PromptSessionNotFoundException exception = await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load(id));

        Assert.Equal(id, exception.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Load_ForAnIdentifierThatIsNotAGuid_ThrowsWithoutReadingTheStore(string id)
    {
        PromptSessionNotFoundException exception = await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load(id));

        Assert.Equal(id, exception.Id);
        store.Verify(mock => mock.Find(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Load_WhenTheProviderDeletedItsSession_KeepsTheStoredSessionAndThrows()
    {
        PromptSessionLink record = Stored();
        engine.Setup(mock => mock.HasSession("abc")).Returns(false);

        PromptSessionNotFoundException exception = await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load(record.Id.ToString("N")));

        Assert.Equal(record.Id.ToString("N"), exception.Id);
        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>()), Times.Never);
    }

    [Fact]
    public async Task Load_WhenTheProviderSessionIsStillThere_KeepsTheStoredSession()
    {
        PromptSessionLink record = Stored();

        await Subject().Load(record.Id.ToString("N"));

        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Load_BeforeTheProviderSessionExists_DoesNotAskTheProvider()
    {
        PromptSessionLink record = Stored() with { AiSessionId = null };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Load(record.Id.ToString("N"));

        engine.Verify(mock => mock.HasSession(It.IsAny<string>()), Times.Never);
        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Load_OfABranchNotPromptedYet_ChecksThatTheOriginalConversationStillExists()
    {
        PromptSessionLink record = Stored() with { AiSessionId = null, ForkedFrom = "origin" };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        engine.Setup(mock => mock.HasSession("origin")).Returns(false);

        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load(record.Id.ToString("N")));

        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
        engine.Setup(mock => mock.HasSession("origin")).Returns(true);
        await Subject().Load(record.Id.ToString("N"));
    }

    [Fact]
    public async Task Delete_RemovesTheProvidersConversationTheScratchFolderAndTheLink()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        PromptSessionLink record = Stored() with { WorkingFolder = folder };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        List<string> order = [];
        engine.Setup(mock => mock.DeleteSession("abc", It.IsAny<CancellationToken>())).Callback(() => order.Add("engine")).Returns(Task.CompletedTask);
        store.Setup(mock => mock.Delete(record.Id)).Callback(() => order.Add("store")).Returns(Task.CompletedTask);

        bool deleted = await Subject().Delete(record.Id.ToString("N"));

        Assert.True(deleted);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(["engine", "store"], order);
    }

    [Fact]
    public async Task Delete_OfASessionNeverLinked_LeavesTheProviderAlone()
    {
        PromptSessionLink record = Stored() with { AiSessionId = null, ForkedFrom = "origin" };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        Assert.True(await Subject().Delete(record.Id.ToString("N")));

        engine.Verify(mock => mock.DeleteSession(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(mock => mock.Delete(record.Id), Times.Once);
    }

    [Fact]
    public async Task Delete_NeverRemovesAFolderThatIsNotOneTheLibraryMade()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"important-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        PromptSessionLink record = Stored() with { WorkingFolder = folder };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Delete(record.Id.ToString("N"));

        Assert.True(Directory.Exists(folder));
        Directory.Delete(folder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Delete_OfSomethingThatIsNotAStoredSession_ReturnsFalse(string id)
    {
        Assert.False(await Subject().Delete(id));
        Assert.False(await Subject().Delete(Guid.NewGuid().ToString("N")));
        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Delete_WhenTheConversationCannotBeDeleted_KeepsTheLinkAndFails()
    {
        PromptSessionLink record = Stored();
        engine.Setup(mock => mock.DeleteSession("abc", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("locked"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => Subject().Delete(record.Id.ToString("N")));

        Assert.Contains("locked", exception.Message);
        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData("Fake")]
    [InlineData("FAKE")]
    public async Task Load_OfASessionWhoseNameIsThatOfTheEngine_IgnoresCase(string name)
    {
        PromptSessionLink record = Stored() with { Provider = name };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Load(record.Id.ToString("N"));

        opener.Verify(mock => mock.Open(record, It.IsAny<IPromptProvider>(), engine.Object, null, It.IsAny<PromptModel?>()), Times.Once);
    }

    [Fact]
    public async Task LoadAndDelete_OfASessionOfAnotherEngine_Throw()
    {
        PromptSessionLink record = Stored() with { Provider = "Other" };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        ArgumentException load = await Assert.ThrowsAsync<ArgumentException>(() => Subject().Load(record.Id.ToString("N")));
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().Delete(record.Id.ToString("N")));

        Assert.Contains("Other", load.Message);
        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
        engine.Verify(mock => mock.DeleteSession(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>()), Times.Never);
    }

    [Fact]
    public async Task GetWindows_AsksTheEngine()
    {
        IReadOnlyList<PromptWindow> status = TestWindows.Claude(session: 0.03);
        engine.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(status);

        Assert.Same(status, await Subject().GetWindows());
    }
}
