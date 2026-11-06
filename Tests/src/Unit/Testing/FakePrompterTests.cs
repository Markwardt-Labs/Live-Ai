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
    public async Task Create_TakesTheKeyOfCodexSetupAndNoneFromLocalSetup()
    {
        FakePrompter prompter = new();

        FakePromptSession codex = (FakePromptSession)await prompter.Create(new CodexSetup { ApiKey = "k" });
        FakePromptSession local = (FakePromptSession)await prompter.Create(new LocalSetup { Overrides = new PromptServerSettings { Models = PromptCapabilityModels.All("m") } });

        Assert.Equal(("k", null), (codex.ApiKey, local.ApiKey));
        Assert.Equal(["Codex", "OpenCode"], (await prompter.List()).Select(info => info.Provider).Order());
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
        Assert.True(sessions[1].IsStarted);
        Assert.False(sessions[0].IsStarted);
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
