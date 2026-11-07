namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PrompterTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptSession> session = new();

    private readonly List<PromptSetup> asked = [];

    private readonly List<string> folders = [];

    public PrompterTests()
    {
        store.Setup(mock => mock.GetWorkspaceFolder(It.IsAny<Guid>())).Returns((Guid id) => Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id:N}"));
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Callback<PromptSessionLink, CancellationToken>((link, _) => folders.Add(link.WorkingFolder)).Returns(Task.CompletedTask);
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(session.Object);
    }

    private Prompter Subject(PromptProvider provider = PromptProvider.Claude)
        => new(store.Object, opener.Object, runner.Object, TimeProvider.System, options =>
        {
            asked.Add(options);
            Mock<IPromptEngine> engine = new();
            engine.SetupGet(mock => mock.Identifier).Returns(provider.ToString());
            engine.SetupGet(mock => mock.Provider).Returns(provider);
            engine.Setup(mock => mock.HasSession(It.IsAny<string>())).Returns(true);
            return engine.Object;
        });

    private void Clean()
    {
        foreach (string folder in folders.Where(folder => folder != AppContext.BaseDirectory))
        {
            Directory.Delete(folder);
        }
    }

    private PromptSessionLink Stored(PromptSetup? options, PromptProvider provider = PromptProvider.Claude, string? aiSessionId = "abc", bool imagesAllowed = false)
    {
        PromptSessionLink link = new() { LastLoaded = TestModels.Loaded, Id = Guid.NewGuid(), Provider = provider, WorkingFolder = AppContext.BaseDirectory, AiSessionId = aiSessionId, Setup = options, IsImageGenerationAllowed = imagesAllowed };
        store.Setup(mock => mock.Find(link.Id, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        return link;
    }

    [Fact]
    public async Task Create_ByTheTypeOfTheSetup_StoresThemWithTheLinkAndOpensTheSessionWithTheKeyAndTheWorkspace()
    {
        using TempFolder workspace = new();
        ClaudeSetup options = new() { ApiKey = "key", WorkspacePath = workspace.Path };

        IPromptSession created = await Subject().Create(options);

        Assert.Same(session.Object, created);
        Assert.Equal([options], asked);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Setup == options && link.Provider == PromptProvider.Claude && link.WorkspacePath == workspace.Path), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), "key"), Times.Once);
    }

    [Fact]
    public async Task Create_WithCodexAndLocalSetup_UsesTheirOwnWorkspaceAndKey()
    {
        using TempFolder workspace = new();
        Prompter prompter = Subject();

        await prompter.Create(new CodexSetup { ApiKey = "codex-key" });
        await prompter.Create(new LocalSetup { Overrides = new PromptServerSettings { Models = PromptCapabilityModels.All("gemma4:26b") }, WorkspacePath = workspace.Path });

        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), "codex-key"), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(link => link.WorkspacePath == workspace.Path), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), null), Times.Once);
        foreach (string folder in folders.Where(folder => folder != workspace.Path))
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Create_WithSetupOfAKindTheLibraryDoesNotKnow_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().Create(new UnknownOptions()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Subject().Create(null!));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ReusesTheEngineForTheSameToolAndMakesAnotherWhenTheEnvironmentOrTheOverridesOfTheSessionChangeIt()
    {
        Prompter prompter = Subject();

        await prompter.Create(new ClaudeSetup { ApiKey = "a" });
        await prompter.Create(new ClaudeSetup { ApiKey = "b" });
        prompter.Environment = new PromptEnvironment { ClaudeCommand = "other-claude" };
        await prompter.Create(new ClaudeSetup());
        prompter.Environment = new PromptEnvironment { ClaudeConfigFolder = "/config", LocalSettings = new PromptServerSettings { Models = PromptCapabilityModels.All("a") } };
        await prompter.Create(new ClaudeSetup());
        await prompter.Create(new LocalSetup());
        await prompter.Create(new LocalSetup());
        await prompter.Create(new LocalSetup { Overrides = new PromptServerSettings { Models = new PromptCapabilityModels { Fast = "a", Balanced = "a", Strong = "a", Max = "b" } } });
        await prompter.Create(new LocalSetup { Overrides = new PromptServerSettings { Contexts = PromptCapabilityContexts.All(4096) } });
        await prompter.Create(new LocalSetup { Overrides = new PromptServerSettings { Contexts = new PromptCapabilityContexts { Max = 4096 } } });
        await prompter.Create(new LocalSetup { Overrides = new PromptServerSettings { Server = new Uri("http://box:11434/v1") } });

        Assert.Equal(8, asked.Count);
        foreach (string folder in folders)
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Create_UsesTheKeyOfTheEnvironmentUnlessTheSetupHasItsOwnAndStoresTheKeyItUsed()
    {
        Prompter prompter = Subject();
        prompter.Environment = new PromptEnvironment { ClaudeApiKey = "claude-env", CodexApiKey = "codex-env" };

        await prompter.Create(new ClaudeSetup());
        await prompter.Create(new ClaudeSetup { ApiKey = "own" });
        await prompter.Create(new CodexSetup());

        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), "claude-env"), Times.Once);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), "own"), Times.Once);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), "codex-env"), Times.Once);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => new ClaudeSetup { ApiKey = "claude-env" }.Equals(link.Setup)), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => new CodexSetup { ApiKey = "codex-env" }.Equals(link.Setup)), It.IsAny<CancellationToken>()), Times.Once);
        foreach (string folder in folders)
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Load_UsesTheKeyTheSessionWasCreatedWithEvenWhenTheEnvironmentHasAnother()
    {
        Prompter prompter = Subject();
        prompter.Environment = new PromptEnvironment { ClaudeApiKey = "claude-env" };
        PromptSessionLink withKey = Stored(new ClaudeSetup { ApiKey = "stored" });
        PromptSessionLink without = Stored(new ClaudeSetup());

        await prompter.Load(withKey.Id.ToString("N"));
        await prompter.Load(without.Id.ToString("N"));

        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.Id == withKey.Id), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), "stored"), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.Id == without.Id), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), null), Times.Once);
    }

    [Fact]
    public async Task Test_UsesTheKeyOfTheEnvironmentWhenTheSetupHasNone()
    {
        Mock<IPromptEngine> engine = new();
        engine.SetupGet(mock => mock.Identifier).Returns("Claude Code");
        engine.Setup(mock => mock.CreateTestStart("claude-env", It.IsAny<CancellationToken>())).ReturnsAsync(new ProcessStartInfo("tool"));
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Finished());
        Prompter prompter = new(store.Object, opener.Object, runner.Object, TimeProvider.System, _ => engine.Object) { Environment = new PromptEnvironment { ClaudeApiKey = "claude-env" } };

        PromptTestResult result = await prompter.Test(new ClaudeSetup());

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void Environment_StartsWithTheDefaultsOfTheToolsAndRefusesNull()
    {
        Prompter prompter = Subject();

        Assert.Equal(("claude", null, "codex", null, "opencode", new PromptServerSettings { Server = PromptServerSettings.DefaultServer }), (prompter.Environment.ClaudeCommand, prompter.Environment.ClaudeConfigFolder, prompter.Environment.CodexCommand, prompter.Environment.CodexConfigFolder, prompter.Environment.LocalCommand, prompter.Environment.LocalSettings));
        Assert.Null(prompter.Environment.ImageGeneration);
        Assert.Equal((null, null), (prompter.Environment.ClaudeApiKey, prompter.Environment.CodexApiKey));
        Assert.Equal((PromptCapability.Balanced, PromptEffort.High, false, false, false, false, null), (prompter.Environment.Capability, prompter.Environment.Effort, prompter.Environment.IsShellAllowed, prompter.Environment.IsSystemAccessAllowed, prompter.Environment.IsWebAllowed, prompter.Environment.IsImageGenerationAllowed, prompter.Environment.Instructions));
        Assert.Throws<ArgumentNullException>(() => prompter.Environment = null!);
    }

    [Fact]
    public async Task Create_StartsTheConfigurationOfTheSessionWithTheConfigurationOfTheEnvironment()
    {
        Prompter prompter = Subject();
        prompter.Environment = new PromptEnvironment { Capability = PromptCapability.Max, Effort = PromptEffort.Low, IsShellAllowed = true, IsSystemAccessAllowed = true, IsWebAllowed = true, Instructions = "Be brief.", IsImageGenerationAllowed = true };

        await prompter.Create(new ClaudeSetup());

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Capability == PromptCapability.Max && link.Effort == PromptEffort.Low && link.IsShellAllowed && link.IsSystemAccessAllowed && link.IsWebAllowed && link.Instructions == "Be brief." && link.IsImageGenerationAllowed), It.IsAny<CancellationToken>()), Times.Once);
        Clean();
    }

    [Fact]
    public async Task Create_UsesTheConfigurationTheEnvironmentHasAtTheTimeAndLoadKeepsWhatTheSessionWasLeftWith()
    {
        Prompter prompter = Subject();
        await prompter.Create(new ClaudeSetup());
        prompter.Environment = new PromptEnvironment { Capability = PromptCapability.Strong, IsSystemAccessAllowed = true };
        await prompter.Create(new ClaudeSetup());
        PromptSessionLink link = Stored(new ClaudeSetup());

        await prompter.Load(link.Id.ToString("N"));

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Capability == PromptCapability.Balanced && !saved.IsSystemAccessAllowed && saved.WorkingFolder != AppContext.BaseDirectory), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Capability == PromptCapability.Strong && saved.IsSystemAccessAllowed && saved.WorkingFolder != AppContext.BaseDirectory), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Id == link.Id && saved.Capability == PromptCapability.Balanced && !saved.IsSystemAccessAllowed), It.IsAny<CancellationToken>()), Times.Once);
        Clean();
    }

    [Fact]
    public async Task Create_RefusesACapabilityOrAnEffortOfTheEnvironmentThatIsNotDefinedAndStoresNothing()
    {
        Prompter prompter = Subject();

        prompter.Environment = new PromptEnvironment { Capability = (PromptCapability)99 };
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Create(new ClaudeSetup()));
        prompter.Environment = new PromptEnvironment { Effort = (PromptEffort)99 };
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Create(new ClaudeSetup()));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithoutConfigurationOnTheEnvironment_StartsWithTheUsualConfiguration()
    {
        await Subject().Create(new CodexSetup());

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Capability == PromptCapability.Balanced && link.Effort == PromptEffort.High && !link.IsShellAllowed && !link.IsSystemAccessAllowed && !link.IsWebAllowed && link.Instructions == null && !link.IsImageGenerationAllowed), It.IsAny<CancellationToken>()), Times.Once);
        foreach (string folder in folders)
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task AnImageSetupOfTheEnvironmentThatIsNotValid_OnlyRefusesTheSessionsThatAllowImageGeneration()
    {
        Prompter prompter = Subject();
        PromptImageSetup invalid = new OpenAiImageSetup { ApiKey = " " };
        prompter.Environment = new PromptEnvironment { IsImageGenerationAllowed = true, ImageGeneration = invalid };
        PromptSessionLink allowed = Stored(new ClaudeSetup(), imagesAllowed: true);
        PromptSessionLink notAllowed = Stored(new ClaudeSetup());

        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Create(new ClaudeSetup()));
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Load(allowed.Id.ToString("N")));
        prompter.Environment = new PromptEnvironment { ImageGeneration = invalid };
        await prompter.Create(new ClaudeSetup());
        await prompter.Load(notAllowed.Id.ToString("N"));

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Id == allowed.Id), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Id == notAllowed.Id), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.Id == notAllowed.Id), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), null), Times.Once);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.Id == allowed.Id), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>()), Times.Never);
        foreach (string folder in folders.Where(folder => folder != AppContext.BaseDirectory))
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Load_ReadsTheSetupOfTheStoredSessionToPickTheToolAndTakesNoSetupOfItsOwn()
    {
        ClaudeSetup options = new() { ApiKey = "stored-key" };
        PromptSessionLink link = Stored(options);

        IPromptSession loaded = await Subject().Load(link.Id.ToString("N"));

        Assert.Same(session.Object, loaded);
        Assert.Equal([options], asked);
        opener.Verify(mock => mock.Open(It.Is<PromptSessionLink>(opened => opened.Id == link.Id), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), "stored-key"), Times.Once);
    }

    [Fact]
    public async Task Load_OfAnUnknownIdentifier_Throws()
    {
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load(Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Load("not a guid"));
    }

    [Fact]
    public async Task LoadAndDelete_OfASessionStoredWithoutSetup_Throw()
    {
        PromptSessionLink link = Stored(null);

        await Assert.ThrowsAsync<PromptSessionException>(() => Subject().Load(link.Id.ToString("N")));
        await Assert.ThrowsAsync<PromptSessionException>(() => Subject().Delete(link.Id.ToString("N")));
    }

    [Fact]
    public async Task LocalWithoutAModelForACapability_IsRefusedWhereItIsUsed()
    {
        Prompter prompter = Subject(PromptProvider.Local);
        LocalSetup blank = new() { Overrides = new PromptServerSettings { Models = new PromptCapabilityModels { Fast = "a", Balanced = " ", Strong = "c", Max = "d" } } };
        LocalSetup missing = new();

        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Create(blank));
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Create(missing));
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Test(blank));
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Test(missing));
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LocalWithTheModelsOfTheEnvironment_NeedsNoneInItsSetup()
    {
        Prompter prompter = Subject(PromptProvider.Local);
        prompter.Environment = new PromptEnvironment { LocalSettings = new PromptServerSettings { Models = PromptCapabilityModels.All("gemma4:26b") } };

        await prompter.Create(new LocalSetup());

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Once);
        foreach (string folder in folders)
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task Load_OfASessionStoredWithLocalSetupWhoseModelsNobodyNames_Throws_ButDeleteDoesNotNeedThem()
    {
        PromptSessionLink link = Stored(new LocalSetup(), PromptProvider.Local);

        await Assert.ThrowsAsync<ArgumentException>(() => Subject(PromptProvider.Local).Load(link.Id.ToString("N")));
        Assert.True(await Subject(PromptProvider.Local).Delete(link.Id.ToString("N")));
    }

    [Fact]
    public async Task Move_UsesTheStoredSetupToPickTheToolAndMovesTheStoredSessionWithoutLoadingIt()
    {
        using TempFolder target = new();
        PromptSessionLink link = Stored(new CodexSetup(), PromptProvider.Codex) with { WorkingFolder = "/gone", WorkspacePath = "/gone" };
        store.Setup(mock => mock.Find(link.Id, It.IsAny<CancellationToken>())).ReturnsAsync(link);

        await Subject(PromptProvider.Codex).Move(link.Id.ToString("N"), target.Path);

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Id == link.Id && saved.WorkspacePath == target.Path && saved.WorkingFolder == target.Path), It.IsAny<CancellationToken>()), Times.Once);
        opener.Verify(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>()), Times.Never);
        Assert.IsType<CodexSetup>(Assert.Single(asked));
    }

    [Fact]
    public async Task Move_OfASessionNobodyKnowsOrWithoutAFolder_IsRefused()
    {
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Subject().Move(Guid.NewGuid().ToString("N"), AppContext.BaseDirectory));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Subject().Move(Guid.NewGuid().ToString("N"), null!));
    }

    [Fact]
    public async Task Delete_OfAnUnknownIdentifier_IsFalse()
    {
        Assert.False(await Subject().Delete(Guid.NewGuid().ToString("N")));
        Assert.False(await Subject().Delete("not a guid"));
    }

    [Fact]
    public async Task Delete_UsesTheStoredSetupToPickTheToolAndRemovesTheLink()
    {
        PromptSessionLink link = Stored(new CodexSetup(), PromptProvider.Codex);

        bool deleted = await Subject(PromptProvider.Codex).Delete(link.Id.ToString("N"));

        Assert.True(deleted);
        store.Verify(mock => mock.Delete(link.Id), Times.Once);
        Assert.IsType<CodexSetup>(Assert.Single(asked));
    }

    [Fact]
    public async Task List_DescribesTheSessionsOfEveryTool()
    {
        Guid claude = Guid.NewGuid();
        DateTimeOffset created = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        store.Setup(mock => mock.List(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PromptSessionLink { Id = claude, LastLoaded = created, Provider = PromptProvider.Claude, WorkingFolder = "/project", WorkspacePath = "/project", AiSessionId = "abc" },
            new PromptSessionLink { LastLoaded = TestModels.Loaded, Id = Guid.NewGuid(), Provider = PromptProvider.Codex, WorkingFolder = "/w" }
        ]);

        IReadOnlyList<PromptSessionInfo> sessions = await Subject().List();

        Assert.Equal(new PromptSessionInfo { Id = claude.ToString("N"), Provider = PromptProvider.Claude, LastLoaded = created, WorkspacePath = "/project" }, sessions[0]);
        Assert.Equal([PromptProvider.Claude, PromptProvider.Codex], sessions.Select(info => info.Provider));
    }

    [Fact]
    public async Task Test_AsksTheToolOfTheSetupWithTheirKey()
    {
        Mock<IPromptEngine> engine = new();
        engine.SetupGet(mock => mock.Identifier).Returns("Claude Code");
        engine.Setup(mock => mock.CreateTestStart("key", It.IsAny<CancellationToken>())).ReturnsAsync(new ProcessStartInfo("tool"));
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Finished());
        Prompter prompter = new(store.Object, opener.Object, runner.Object, TimeProvider.System, _ => engine.Object);

        PromptTestResult result = await prompter.Test(new ClaudeSetup { ApiKey = "key" });

        Assert.True(result.IsAvailable);
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Test(new UnknownOptions()));
    }

    private async IAsyncEnumerable<PromptEngineEvent> Finished()
    {
        await Task.Yield();
        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished };
    }

    private sealed record UnknownOptions : PromptSetup;
}
