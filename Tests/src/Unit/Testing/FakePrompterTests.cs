namespace Markwardt.LiveAi.Tests.Unit.Testing;

public sealed class FakePrompterTests
{
    [Fact]
    public async Task Create_MakesAFakeSessionWithTheKeyAndTheWorkspaceOfTheSetupAndRecordsThem()
    {
        using TempFolder folder = new();
        FakePrompter prompter = new();
        ClaudeSetup options = new() { ApiKey = "key", WorkspacePath = folder.Path };

        FakePromptSession session = (FakePromptSession)await prompter.Create(options);

        Assert.Equal(("key", folder.Path), (session.ApiKey, session.WorkspacePath));
        Assert.Equal([options], prompter.Created);
        Assert.Equal([session], prompter.Sessions);
    }

    [Fact]
    public async Task Create_UsesTheKeyOfTheEnvironmentUnlessTheSetupHasItsOwn()
    {
        FakePrompter prompter = new() { Environment = new PromptEnvironment { ClaudeApiKey = "claude-env", CodexApiKey = "codex-env" } };

        FakePromptSession claude = (FakePromptSession)await prompter.Create(new ClaudeSetup());
        FakePromptSession own = (FakePromptSession)await prompter.Create(new ClaudeSetup { ApiKey = "own" });
        FakePromptSession codex = (FakePromptSession)await prompter.Create(new CodexSetup());

        Assert.Equal(("claude-env", "own", "codex-env"), (claude.ApiKey, own.ApiKey, codex.ApiKey));
    }

    [Fact]
    public async Task Create_StartsTheConfigurationOfTheSessionWithTheConfigurationOfTheEnvironment()
    {
        FakePrompter prompter = new() { Environment = new PromptEnvironment { Capability = PromptCapability.Strong, Effort = PromptEffort.Low, IsShellAllowed = true, IsSystemAccessAllowed = true, IsWebAllowed = true, IsImageGenerationAllowed = true, Instructions = "Be brief." } };

        FakePromptSession session = (FakePromptSession)await prompter.Create(new ClaudeSetup());

        Assert.Equal((PromptCapability.Strong, PromptEffort.Low, true, true, true, true, "Be brief."), (session.Configuration.Capability, session.Configuration.Effort, session.Configuration.IsShellAllowed, session.Configuration.IsSystemAccessAllowed, session.Configuration.IsWebAllowed, session.Configuration.IsImageGenerationAllowed, session.Configuration.Instructions));
    }

    [Fact]
    public async Task Create_TakesTheKeyOfCodexSetupAndNoneFromLocalSetup()
    {
        FakePrompter prompter = new();

        FakePromptSession codex = (FakePromptSession)await prompter.Create(new CodexSetup { ApiKey = "k" });
        FakePromptSession local = (FakePromptSession)await prompter.Create(new LocalSetup { Overrides = new PromptServerSettings { Models = PromptCapabilityModels.All("m") } });

        Assert.Equal(("k", null), (codex.ApiKey, local.ApiKey));
        Assert.Equal([PromptProvider.Codex, PromptProvider.Local], (await prompter.List()).Select(info => info.Provider).Order());
        Assert.Equal((PromptProvider.Codex, PromptProvider.Local), (codex.Provider, local.Provider));
    }

    [Fact]
    public async Task Create_CallsOnCreateBeforeReturningTheSessionSoItsAnswersCanBeScripted()
    {
        FakePrompter prompter = new() { OnCreate = session => session.Reply("scripted") };

        IPromptSession session = await prompter.Create(new ClaudeSetup());

        Assert.Equal("scripted", await session.Request("hi"));
    }

    [Fact]
    public async Task Create_RefusesNullAndSetupOfAKindTheLibraryDoesNotKnow()
    {
        FakePrompter prompter = new();

        await Assert.ThrowsAsync<ArgumentNullException>(() => prompter.Create(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => prompter.Create(new UnknownOptions()));
    }

    [Fact]
    public async Task List_DescribesTheSessionsNewestFirstAndSaysWhichWerePrompted()
    {
        ManualTime clock = new();
        FakePrompter prompter = new() { Time = clock };
        FakePromptSession first = (FakePromptSession)await prompter.Create(new ClaudeSetup());
        clock.Advance(TimeSpan.FromMinutes(1));
        await prompter.Create(new ClaudeSetup());
        first.Reply("x");
        await first.Request("go");

        IReadOnlyList<PromptSessionInfo> sessions = await prompter.List();

        Assert.Equal(2, sessions.Count);
        Assert.Equal(first.Id, sessions[1].Id);
    }

    [Fact]
    public async Task Load_GivesTheSameSessionAndResumesOneThatWasDisposed()
    {
        FakePrompter prompter = new();
        IPromptSession created = await prompter.Create(new ClaudeSetup());

        Assert.Same(created, await prompter.Load(created.Id));
        await created.DisposeAsync();
        IPromptSession resumed = await prompter.Load(created.Id);

        Assert.NotSame(created, resumed);
        Assert.Equal(created.Id, resumed.Id);
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => prompter.Load("missing"));
    }

    [Fact]
    public async Task Move_PutsASessionThatIsClosedInAnotherFolderAndListShowsIt()
    {
        using TempFolder first = new();
        using TempFolder second = new();
        FakePrompter prompter = new();
        IPromptSession created = await prompter.Create(new ClaudeSetup { WorkspacePath = first.Path });
        await created.DisposeAsync();

        await prompter.Move(created.Id, second.Path);

        Assert.Equal(second.Path, Assert.Single(await prompter.List()).WorkspacePath);
        await using IPromptSession loaded = await prompter.Load(created.Id);
        Assert.Equal(second.Path, loaded.WorkspacePath);
    }

    [Fact]
    public async Task Load_RecordsWhenTheSessionWasLoadedAndListOrdersByItButMoveLeavesItAlone()
    {
        ManualTime clock = new();
        using TempFolder folder = new();
        FakePrompter prompter = new() { Time = clock };
        IPromptSession first = await prompter.Create(new ClaudeSetup());
        clock.Advance(TimeSpan.FromMinutes(1));
        IPromptSession second = await prompter.Create(new ClaudeSetup());
        DateTimeOffset created = (await prompter.List()).Single(info => info.Id == first.Id).LastLoaded;
        clock.Advance(TimeSpan.FromMinutes(1));

        await prompter.Move(first.Id, folder.Path);

        Assert.Equal([second.Id, first.Id], (await prompter.List()).Select(info => info.Id));
        Assert.Equal(created, (await prompter.List()).Single(info => info.Id == first.Id).LastLoaded);

        await using IPromptSession loaded = await prompter.Load(first.Id);

        IReadOnlyList<PromptSessionInfo> sessions = await prompter.List();
        Assert.Equal([first.Id, second.Id], sessions.Select(info => info.Id));
        Assert.Equal(clock.GetUtcNow(), sessions[0].LastLoaded);
    }

    [Fact]
    public async Task Move_RefusesAFolderThatIsNotThereAndASessionThatIsNot()
    {
        FakePrompter prompter = new();
        IPromptSession created = await prompter.Create(new ClaudeSetup());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => prompter.Move(created.Id, "/no/such/folder"));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => prompter.Move(Guid.NewGuid().ToString("N"), AppContext.BaseDirectory));
    }

    [Fact]
    public async Task Delete_ForgetsTheSessionAndSaysWhetherThereWasOne()
    {
        FakePrompter prompter = new();
        IPromptSession created = await prompter.Create(new ClaudeSetup());

        Assert.True(await prompter.Delete(created.Id));
        Assert.False(await prompter.Delete(created.Id));
        Assert.Empty(await prompter.List());
    }

    [Fact]
    public async Task Test_RecordsTheSetupAndAnswersWithTheScriptedResult()
    {
        FakePrompter prompter = new();
        ClaudeSetup options = new();

        Assert.True((await prompter.Test(options)).IsAvailable);
        PromptTestResult failed = await prompter.FailTests("not signed in").Test(options);

        Assert.Equal(("not signed in", false), (failed.Error, failed.IsAvailable));
        Assert.Equal([options, options], prompter.Tests);
    }

    [Fact]
    public async Task Create_AfterACancellation_Throws()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FakePrompter().Create(new ClaudeSetup(), cancellation.Token));
    }

    private sealed record UnknownOptions : PromptSetup;
}
