namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptToolServiceTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private readonly Mock<IPromptSession> session = new();

    private readonly List<string> folders = [];

    public PromptToolServiceTests()
    {
        store.Setup(mock => mock.GetWorkspaceFolder(It.IsAny<Guid>())).Returns((Guid id) => Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id:N}"));
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.HasSession("abc")).Returns(true);
    }

    private PromptToolService Subject()
    {
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(session.Object);
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((record, _) => folders.Add(record.WorkingFolder)).Returns(Task.CompletedTask);
        return new PromptToolService(engine.Object, store.Object, opener.Object, runner.Object);
    }

    private PromptSessionLink Stored()
    {
        PromptSessionLink record = new() { LastLoaded = TestModels.Loaded, Id = Guid.NewGuid(), Provider = PromptProvider.Claude, WorkingFolder = AppContext.BaseDirectory, AiSessionId = "abc" };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        return record;
    }

    [Fact]
    public async Task List_DescribesTheStoredSessionsOfItsOwnEngineOnly()
    {
        Guid mine = Guid.NewGuid();
        DateTimeOffset created = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        engine.SetupGet(mock => mock.Provider).Returns(PromptProvider.Codex);
        store.Setup(mock => mock.List(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PromptSessionLink { Id = mine, LastLoaded = created, Provider = PromptProvider.Codex, WorkingFolder = "/project", WorkspacePath = "/project", AiSessionId = "abc" },
            new PromptSessionLink { LastLoaded = TestModels.Loaded, Id = Guid.NewGuid(), Provider = PromptProvider.Local, WorkingFolder = "/w" },
            new PromptSessionLink { LastLoaded = TestModels.Loaded, Id = Guid.NewGuid(), Provider = PromptProvider.Codex, WorkingFolder = "/w" }
        ]);

        IReadOnlyList<PromptSessionInfo> sessions = await Subject().List();

        Assert.Equal(2, sessions.Count);
        Assert.Equal(new PromptSessionInfo { Id = mine.ToString("N"), Provider = PromptProvider.Codex, LastLoaded = created, WorkspacePath = "/project" }, sessions[0]);
    }

    [Fact]
    public async Task Create_RecordsWhenTheSessionWasMadeOnTheClockOfTheProvider()
    {
        ManualTime clock = new();
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(session.Object);
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((record, _) => folders.Add(record.WorkingFolder)).Returns(Task.CompletedTask);

        await new PromptToolService(engine.Object, store.Object, opener.Object, runner.Object, clock).Create(TestModels.Options);

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.LastLoaded == clock.GetUtcNow()), It.IsAny<CancellationToken>()), Times.Once);
        Directory.Delete(folders.Single());
    }

    [Fact]
    public async Task Create_StoresTheLinkUnderANewIdentifierAndOpensTheSession()
    {
        IPromptSession created = await Subject().Create(new PromptToolServiceOptions { ApiKey = "key" });

        Assert.Same(session.Object, created);
        Func<PromptSessionLink, bool> expected = record => record.Id != Guid.Empty && record.Provider == PromptProvider.Claude && record.AiSessionId == null && record.ForkedFrom == null;
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(record => expected(record)), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(record => expected(record)), It.IsAny<IPromptToolService>(), engine.Object, "key"), Times.Once);
        Directory.Delete(folders.Single());
    }

    [Fact]
    public async Task Create_WithAWorkspacePath_StoresItAsTheWorkingFolderAndMakesNoFolder()
    {
        using TempFolder workspace = new();

        await Subject().Create(new PromptToolServiceOptions { WorkspacePath = workspace.Path });

        Func<PromptSessionLink, bool> expected = link => link.WorkingFolder == workspace.Path && link.WorkspacePath == workspace.Path;
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => expected(link)), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(link => expected(link)), It.IsAny<IPromptToolService>(), engine.Object, null), Times.Once);
        Assert.Equal([workspace.Path], folders);
    }

    [Fact]
    public async Task Create_WithAWorkspacePathThatDoesNotExist_ThrowsAndStoresNothing()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Subject().Create(new PromptToolServiceOptions { WorkspacePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}") }));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithoutAWorkspacePath_StoresNoWorkspace()
    {
        await Subject().Create(TestModels.Options);

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.WorkspacePath == null && !link.IsSystemAccessAllowed), It.IsAny<CancellationToken>()), Times.Once);
        Directory.Delete(folders.Single());
    }

    [Fact]
    public async Task Load_WithAWorkspacePath_IsRefusedBecauseTheWorkspaceIsStoredWithTheSession()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().Load(Guid.NewGuid().ToString("N"), new PromptToolServiceOptions { WorkspacePath = "/somewhere" }));

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
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.Id == record.Id), It.IsAny<IPromptToolService>(), engine.Object, null), Times.Once);
    }

    [Fact]
    public async Task Load_StoresWhenTheSessionWasLoadedOnTheClockAndOpensTheSessionWithIt()
    {
        ManualTime clock = new();
        PromptSessionLink record = Stored() with { LastLoaded = clock.GetUtcNow().AddDays(-3) };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(session.Object);

        await new PromptToolService(engine.Object, store.Object, opener.Object, runner.Object, clock).Load(record.Id.ToString("N"));

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Id == record.Id && saved.LastLoaded == clock.GetUtcNow()), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.LastLoaded == clock.GetUtcNow()), It.IsAny<IPromptToolService>(), engine.Object, null), Times.Once);
    }

    [Fact]
    public async Task Load_WhenTheSessionCannotBeOpened_DoesNotRecordALoad()
    {
        PromptSessionLink record = Stored();
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Throws(new DirectoryNotFoundException("gone"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new PromptToolService(engine.Object, store.Object, opener.Object, runner.Object).Load(record.Id.ToString("N")));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Load_StillOpensTheSessionWhenTheTimeOfTheLoadCannotBeStored()
    {
        PromptSessionLink record = Stored();
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("read only"));
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(session.Object);

        IPromptSession loaded = await new PromptToolService(engine.Object, store.Object, opener.Object, runner.Object).Load(record.Id.ToString("N"));

        Assert.Same(session.Object, loaded);
    }

    [Fact]
    public async Task Delete_NeverRemovesAWorkspaceFolderTheCallerGave()
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
    public async Task Move_BringsTheConversationToTheFolderAndStoresTheLinkWithItKeepingEverythingElse()
    {
        using TempFolder target = new();
        PromptSessionLink record = Stored() with { WorkingFolder = "/gone", WorkspacePath = "/gone", IsSystemAccessAllowed = true, IsShellAllowed = true, Instructions = "keep" };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Move(record.Id.ToString("N"), target.Path);

        engine.Verify(mock => mock.MoveSession("abc", target.Path, It.IsAny<CancellationToken>()), Times.Once);
        PromptSessionLink expected = record with { WorkingFolder = target.Path, WorkspacePath = target.Path };
        store.Verify(mock => mock.Save(expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Move_ForASessionWithoutAConversationYetOnlyStoresTheFolder()
    {
        using TempFolder target = new();
        PromptSessionLink record = Stored() with { AiSessionId = null };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Move(record.Id.ToString("N"), target.Path);

        engine.Verify(mock => mock.MoveSession(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.WorkspacePath == target.Path), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Move_ForABranchThatHasNotPromptedYetMovesTheConversationItIsMadeFrom()
    {
        using TempFolder target = new();
        PromptSessionLink record = Stored() with { AiSessionId = null, ForkedFrom = "origin" };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        await Subject().Move(record.Id.ToString("N"), target.Path);

        engine.Verify(mock => mock.MoveSession("origin", target.Path, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Move_RemovesTheFolderTheLibraryMadeForASessionThatHadNoneButNeverOneTheCallerGave()
    {
        using TempFolder target = new();
        using TempFolder given = new();
        string made = Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(made);
        PromptSessionLink automatic = Stored() with { WorkingFolder = made, WorkspacePath = null };
        PromptSessionLink chosen = Stored() with { WorkingFolder = given.Path, WorkspacePath = given.Path };
        store.Setup(mock => mock.Find(automatic.Id, It.IsAny<CancellationToken>())).ReturnsAsync(automatic);
        store.Setup(mock => mock.GetWorkspaceFolder(automatic.Id)).Returns(made);
        store.Setup(mock => mock.Find(chosen.Id, It.IsAny<CancellationToken>())).ReturnsAsync(chosen);

        await Subject().Move(automatic.Id.ToString("N"), target.Path);
        await Subject().Move(chosen.Id.ToString("N"), target.Path);

        Assert.False(Directory.Exists(made));
        Assert.True(Directory.Exists(given.Path));
    }

    [Fact]
    public async Task Move_ToAFolderThatDoesNotExist_ChangesNothing()
    {
        PromptSessionLink record = Stored();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Subject().Move(record.Id.ToString("N"), Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{Guid.NewGuid():N}")));

        engine.Verify(mock => mock.MoveSession(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Move_WhenTheToolCannotBringTheConversation_StoresNothing()
    {
        using TempFolder target = new();
        PromptSessionLink record = Stored();
        engine.Setup(mock => mock.MoveSession("abc", target.Path, It.IsAny<CancellationToken>())).ThrowsAsync(new PromptSessionException("The Claude Code session abc to move was not found."));

        await Assert.ThrowsAsync<PromptSessionException>(() => Subject().Move(record.Id.ToString("N"), target.Path));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Move_OfAnUnknownSessionOrOneOfAnotherTool_IsRefused()
    {
        using TempFolder target = new();
        PromptSessionLink other = Stored() with { Provider = PromptProvider.Codex };
        store.Setup(mock => mock.Find(other.Id, It.IsAny<CancellationToken>())).ReturnsAsync(other);

        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Move(Guid.NewGuid().ToString("N"), target.Path));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Move("not a guid", target.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().Move(other.Id.ToString("N"), target.Path));
    }

    [Fact]
    public async Task Create_WhenTheLinkCannotBeStored_LeavesNoAutomaticWorkspaceBehind()
    {
        string? folder = null;
        PromptToolService subject = Subject();
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((link, _) => folder = link.WorkingFolder).ThrowsAsync(new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => subject.Create(TestModels.Options));

        Assert.NotNull(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Create_GivesEverySessionItsOwnIdentifierAndWorkingFolder()
    {
        List<Guid> ids = [];
        PromptToolService service = Subject();
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((record, _) =>
        {
            ids.Add(record.Id);
            folders.Add(record.WorkingFolder);
        }).Returns(Task.CompletedTask);

        await service.Create(TestModels.Options);
        await service.Create(TestModels.Options);

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

        IPromptSession loaded = await Subject().Load(record.Id.ToString("N"), new PromptToolServiceOptions { ApiKey = "key" });

        Assert.Same(session.Object, loaded);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.Id == record.Id), It.IsAny<IPromptToolService>(), engine.Object, "key"), Times.Once);
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
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>()), Times.Never);
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
    public async Task Delete_RemovesTheProvidersConversationTheAutomaticWorkspaceAndTheLink()
    {
        PromptSessionLink created = Stored();
        string folder = Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{created.Id:N}");
        Directory.CreateDirectory(folder);
        PromptSessionLink record = created with { WorkingFolder = folder };
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

    [Fact]
    public async Task LoadAndDelete_OfASessionOfAnotherEngine_Throw()
    {
        PromptSessionLink record = Stored() with { Provider = PromptProvider.Local };
        store.Setup(mock => mock.Find(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        ArgumentException load = await Assert.ThrowsAsync<ArgumentException>(() => Subject().Load(record.Id.ToString("N")));
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().Delete(record.Id.ToString("N")));

        Assert.Contains("Local", load.Message);
        store.Verify(mock => mock.Delete(It.IsAny<Guid>()), Times.Never);
        engine.Verify(mock => mock.DeleteSession(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Test_ReportsTheWindowsOfTheEngineWhenItAnswers()
    {
        IReadOnlyList<PromptWindow> status = TestWindows.Claude(session: 0.03);
        engine.Setup(mock => mock.CreateTestStart(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ProcessStartInfo("tool"));
        engine.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(status);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Finished());

        PromptTestResult result = await Subject().Test();

        Assert.True(result.IsAvailable);
        Assert.Same(status, result.Windows);
    }

    private PromptModel Model(string id, params string[] efforts)
        => new() { Id = id, Settings = efforts.Length == 0 ? [] : [PromptModelSetting.Choose("effort", "Effort", efforts)] };

    [Fact]
    public async Task Select_ChoosesTheModelOfTheCapabilityAndTheEffortLevelOfTheEffort()
    {
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Model("haiku", "low", "high"), Model("sonnet", "low", "medium", "high"), Model("opus", "low", "high")]);

        (PromptModel model, IReadOnlyDictionary<string, string> settings) = await Subject().Select(PromptCapability.Balanced, PromptEffort.High);

        Assert.Equal(("sonnet", "high"), (model.Id, settings["effort"]));
    }

    [Fact]
    public async Task Select_AsksTheEngineOnceForALifetimeAndOncePerApiKey()
    {
        ManualTime clock = new();
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Model("sonnet")]);
        PromptToolService service = new(engine.Object, store.Object, opener.Object, runner.Object, clock) { ModelsLifetime = TimeSpan.FromMinutes(1) };

        await service.Select(PromptCapability.Fast, PromptEffort.Low);
        await service.Select(PromptCapability.Max, PromptEffort.Max);
        await service.Select(PromptCapability.Max, PromptEffort.Max, "key");
        engine.Verify(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        clock.Advance(TimeSpan.FromMinutes(2));
        await service.Select(PromptCapability.Fast, PromptEffort.Low);

        engine.Verify(mock => mock.GetModels(null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        engine.Verify(mock => mock.GetModels("key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Select_WhenTheEngineCannotListItsModels_UsesTheOnesItKnowsAndAsksAgainNextTime()
    {
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        engine.SetupGet(mock => mock.FallbackModels).Returns([Model("fallback")]);
        PromptToolService service = Subject();

        (PromptModel first, _) = await service.Select(PromptCapability.Max, PromptEffort.Max);
        await service.Select(PromptCapability.Max, PromptEffort.Max);

        Assert.Equal("fallback", first.Id);
        engine.Verify(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Select_WithNoModelAtAll_FailsAndSaysWhatToCheck()
    {
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => Subject().Select(PromptCapability.Fast, PromptEffort.Low));

        Assert.Contains("Check that it is installed and signed in", exception.Message);
    }

    [Theory]
    [InlineData(PromptCapability.Fast, "ollama/small")]
    [InlineData(PromptCapability.Balanced, "ollama/medium")]
    [InlineData(PromptCapability.Strong, "ollama/large")]
    [InlineData(PromptCapability.Max, "ollama/huge")]
    public async Task Select_RunsTheModelTheSetupMapToTheCapability(PromptCapability capability, string expected)
    {
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Model("ollama/huge"), Model("ollama/large"), Model("ollama/medium"), Model("ollama/small")]);
        engine.SetupGet(mock => mock.PreferredModels).Returns(new PromptCapabilityModels { Fast = "small", Balanced = "medium", Strong = "large", Max = "huge" });

        (PromptModel model, _) = await Subject().Select(capability, PromptEffort.Medium);

        Assert.Equal(expected, model.Id);
    }

    [Fact]
    public async Task Select_HonorsThePreferredModelAndTheOverridesOfTheEngine()
    {
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Model("a"), new PromptModel { Id = "ollama/gemma", Settings = [PromptModelSetting.Count("context", "Context", 1024, 65536, 4096)] }]);
        engine.SetupGet(mock => mock.PreferredModels).Returns(PromptCapabilityModels.All("gemma"));
        engine.Setup(mock => mock.ModelOverrides(PromptCapability.Fast)).Returns(new Dictionary<string, string> { ["context"] = "16384" });

        (PromptModel model, IReadOnlyDictionary<string, string> settings) = await Subject().Select(PromptCapability.Fast, PromptEffort.Low);

        Assert.Equal(("ollama/gemma", "16384"), (model.Id, settings["context"]));
    }

    [Fact]
    public async Task Select_AsksTheEngineForTheOverridesOfTheCapabilityItChoosesFor()
    {
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([new PromptModel { Id = "ollama/gemma", Settings = [PromptModelSetting.Count("context", "Context", 1024, 65536, 4096)] }]);
        engine.SetupGet(mock => mock.PreferredModels).Returns(PromptCapabilityModels.All("gemma"));
        engine.Setup(mock => mock.ModelOverrides(PromptCapability.Fast)).Returns(new Dictionary<string, string> { ["context"] = "8192" });
        engine.Setup(mock => mock.ModelOverrides(PromptCapability.Max)).Returns(new Dictionary<string, string> { ["context"] = "32768" });

        (_, IReadOnlyDictionary<string, string> fast) = await Subject().Select(PromptCapability.Fast, PromptEffort.Low);
        (_, IReadOnlyDictionary<string, string> max) = await Subject().Select(PromptCapability.Max, PromptEffort.Low);
        (_, IReadOnlyDictionary<string, string> balanced) = await Subject().Select(PromptCapability.Balanced, PromptEffort.Low);

        Assert.Equal(("8192", "32768", "4096"), (fast["context"], max["context"], balanced["context"]));
    }

    private async IAsyncEnumerable<PromptEngineEvent> Finished()
    {
        await Task.Yield();
        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished };
    }
}
