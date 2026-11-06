namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PrompterTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptSession> session = new();

    private readonly List<PromptSessionOptions> asked = [];

    private readonly List<string> folders = [];

    public PrompterTests()
    {
        store.Setup(mock => mock.GetWorkspaceFolder(It.IsAny<Guid>())).Returns((Guid id) => Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id:N}"));
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((link, _) => folders.Add(link.WorkingFolder)).Returns(Task.CompletedTask);
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(session.Object);
    }

    private Prompter Subject(string identifier = "Claude Code")
        => new(store.Object, opener.Object, runner.Object, TimeProvider.System, options =>
        {
            asked.Add(options);
            Mock<IPromptEngine> engine = new();
            engine.SetupGet(mock => mock.Identifier).Returns(identifier);
            engine.Setup(mock => mock.HasSession(It.IsAny<string>())).Returns(true);
            return engine.Object;
        });

    private PromptSessionLink Stored(PromptSessionOptions? options, string provider = "Claude Code", string? aiSessionId = "abc")
    {
        PromptSessionLink link = new() { Id = Guid.NewGuid(), Provider = provider, WorkingFolder = AppContext.BaseDirectory, AiSessionId = aiSessionId, Options = options };
        store.Setup(mock => mock.Find(link.Id, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        return link;
    }

    [Fact]
    public async Task Create_ByTheTypeOfTheOptions_StoresThemWithTheLinkAndOpensTheSessionWithTheKeyAndTheWorkspace()
    {
        using TempFolder workspace = new();
        ClaudeSessionOptions options = new() { ApiKey = "key", WorkspacePath = workspace.Path };

        IPromptSession created = await Subject().Create(options);

        Assert.Same(session.Object, created);
        Assert.Equal([options], asked);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Options == options && link.Provider == "Claude Code" && link.WorkspacePath == workspace.Path), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), "key"), Times.Once);
    }

    [Fact]
    public async Task Create_WithCodexAndOllamaOptions_UsesTheirOwnWorkspaceAndKey()
    {
        using TempFolder workspace = new();
        Prompter prompter = Subject("Other");

        await prompter.Create(new CodexSessionOptions { ApiKey = "codex-key" });
        await prompter.Create(new OllamaSessionOptions { Models = PromptCapabilityModels.All("gemma4:26b"), WorkspacePath = workspace.Path });

        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), "codex-key"), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(link => link.WorkspacePath == workspace.Path), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), null), Times.Once);
        foreach (string folder in folders.Where(folder => folder != workspace.Path))
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Create_WithOptionsOfAKindTheLibraryDoesNotKnow_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().Create(new UnknownOptions()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Subject().Create(null!));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ReusesTheEngineForTheSameToolAndMakesAnotherForAnotherCommandOrConfigFolder()
    {
        Prompter prompter = Subject();

        await prompter.Create(new ClaudeSessionOptions { ApiKey = "a" });
        await prompter.Create(new ClaudeSessionOptions { ApiKey = "b" });
        await prompter.Create(new ClaudeSessionOptions { Command = "other-claude" });
        await prompter.Create(new ClaudeSessionOptions { ConfigFolder = "/config" });
        await prompter.Create(new OllamaSessionOptions { Models = PromptCapabilityModels.All("a") });
        await prompter.Create(new OllamaSessionOptions { Models = PromptCapabilityModels.All("a") });
        await prompter.Create(new OllamaSessionOptions { Models = new PromptCapabilityModels { Fast = "a", Balanced = "a", Strong = "a", Max = "b" } });

        Assert.Equal(5, asked.Count);
        foreach (string folder in folders)
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Load_ReadsTheOptionsOfTheStoredSessionToPickTheToolAndTakesNoOptionsOfItsOwn()
    {
        ClaudeSessionOptions options = new() { Command = "my-claude" };
        PromptSessionLink link = Stored(options);

        IPromptSession loaded = await Subject().Load(link.Id.ToString("N"));

        Assert.Same(session.Object, loaded);
        Assert.Equal([options], asked);
        opener.Verify(mock => mock.Open(link, It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), null), Times.Once);
    }

    [Fact]
    public async Task Load_OfAnUnknownIdentifier_Throws()
    {
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load(Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load("not a guid"));
    }

    [Fact]
    public async Task LoadAndDelete_OfASessionStoredWithoutOptions_Throw()
    {
        PromptSessionLink link = Stored(null);

        await Assert.ThrowsAsync<PromptSessionException>(() => Subject().Load(link.Id.ToString("N")));
        await Assert.ThrowsAsync<PromptSessionException>(() => Subject().Delete(link.Id.ToString("N")));
    }

    [Fact]
    public async Task OllamaOptionsWithoutAModelForACapability_AreRefusedWhereTheyAreUsed()
    {
        Prompter prompter = Subject("OpenCode");
        OllamaSessionOptions blank = new() { Models = new PromptCapabilityModels { Fast = "a", Balanced = " ", Strong = "c", Max = "d" } };
        OllamaSessionOptions missing = new() { Models = null! };

        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Create(blank));
        await Assert.ThrowsAsync<ArgumentNullException>(() => prompter.Create(missing));
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Test(blank));
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoadAndDelete_OfASessionStoredWithOllamaOptionsWithoutModels_Throw()
    {
        PromptSessionLink link = Stored(new OllamaSessionOptions { Models = null! }, "OpenCode");

        await Assert.ThrowsAsync<PromptSessionException>(() => Subject("OpenCode").Load(link.Id.ToString("N")));
        await Assert.ThrowsAsync<PromptSessionException>(() => Subject("OpenCode").Delete(link.Id.ToString("N")));
    }

    [Fact]
    public async Task Delete_OfAnUnknownIdentifier_IsFalse()
    {
        Assert.False(await Subject().Delete(Guid.NewGuid().ToString("N")));
        Assert.False(await Subject().Delete("not a guid"));
    }

    [Fact]
    public async Task Delete_UsesTheStoredOptionsToPickTheToolAndRemovesTheLink()
    {
        PromptSessionLink link = Stored(new CodexSessionOptions(), "Codex");

        bool deleted = await Subject("Codex").Delete(link.Id.ToString("N"));

        Assert.True(deleted);
        store.Verify(mock => mock.Delete(link.Id), Times.Once);
        Assert.IsType<CodexSessionOptions>(Assert.Single(asked));
    }

    [Fact]
    public async Task List_DescribesTheSessionsOfEveryTool()
    {
        Guid claude = Guid.NewGuid();
        DateTimeOffset created = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        store.Setup(mock => mock.List(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PromptSessionLink { Id = claude, Created = created, Provider = "Claude Code", WorkingFolder = "/project", WorkspacePath = "/project", AiSessionId = "abc" },
            new PromptSessionLink { Id = Guid.NewGuid(), Provider = "Codex", WorkingFolder = "/w" }
        ]);

        IReadOnlyList<PromptSessionInfo> sessions = await Subject().List();

        Assert.Equal(new PromptSessionInfo { Id = claude.ToString("N"), Provider = "Claude Code", Created = created, WorkspacePath = "/project", IsStarted = true }, sessions[0]);
        Assert.Equal(["Claude Code", "Codex"], sessions.Select(info => info.Provider));
        Assert.False(sessions[1].IsStarted);
    }

    [Fact]
    public async Task Test_AsksTheToolOfTheOptionsWithTheirKey()
    {
        Mock<IPromptEngine> engine = new();
        engine.SetupGet(mock => mock.Identifier).Returns("Claude Code");
        engine.Setup(mock => mock.CreateTestStart("key", It.IsAny<CancellationToken>())).ReturnsAsync(new ProcessStartInfo("tool"));
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Finished());
        Prompter prompter = new(store.Object, opener.Object, runner.Object, TimeProvider.System, _ => engine.Object);

        PromptTestResult result = await prompter.Test(new ClaudeSessionOptions { ApiKey = "key" });

        Assert.True(result.IsAvailable);
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Test(new UnknownOptions()));
    }

    private async IAsyncEnumerable<PromptEngineEvent> Finished()
    {
        await Task.Yield();
        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished };
    }

    private sealed record UnknownOptions : PromptSessionOptions;
}
