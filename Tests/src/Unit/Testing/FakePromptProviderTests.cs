namespace Markwardt.LiveAi.Tests.Unit.Testing;

public sealed class FakePromptProviderTests
{
    [Fact]
    public async Task CreateMakesSessionsOnTheProviderWithTheKeyAndLetsTheTestScriptThem()
    {
        FakePromptProvider provider = new() { OnCreate = session => session.Reply("scripted") };

        IPromptSession created = await provider.Create(new PromptSessionOptions { ApiKey = "key" });

        Assert.Equal("scripted", await created.Request("go"));
        FakePromptSession fake = Assert.Single(provider.Sessions);
        Assert.Same(created, fake);
        Assert.Equal((provider, "key"), (fake.Provider, fake.ApiKey));
    }

    [Fact]
    public async Task CreateWithAWorkspacePathSetsTheWorkspaceOfTheSession()
    {
        using TempFolder folder = new();
        FakePromptProvider provider = new();

        IPromptSession session = await provider.Create(new PromptSessionOptions { WorkspacePath = folder.Path, Sandbox = false });

        Assert.Equal(folder.Path, Assert.IsAssignableFrom<IDiskStore>(session.Configuration.Workspace).Path);
    }

    [Fact]
    public async Task ListDescribesTheSessionsNewestFirstAndTracksWhetherTheyWereStarted()
    {
        ManualTime clock = new();
        using TempFolder folder = new();
        FakePromptProvider provider = new() { Time = clock };
        IPromptSession first = await provider.Create();
        clock.Advance(TimeSpan.FromHours(1));
        IPromptSession second = await provider.Create(new PromptSessionOptions { WorkspacePath = folder.Path });
        await first.Request("go").ContinueWith(_ => { });

        IReadOnlyList<PromptSessionInfo> sessions = await provider.List();

        Assert.Equal([second.Id, first.Id], sessions.Select(info => info.Id));
        Assert.Equal(folder.Path, sessions[0].WorkspacePath);
        Assert.Equal((false, true), (sessions[0].IsStarted, sessions[1].IsStarted));
        Assert.Equal(clock.GetUtcNow(), sessions[0].Created);
        Assert.Equal(clock.GetUtcNow().AddHours(-1), sessions[1].Created);
    }

    [Fact]
    public async Task ListLeavesOutDeletedSessionsAndKeepsAResumedOne()
    {
        FakePromptProvider provider = new();
        IPromptSession kept = await provider.Create();
        IPromptSession gone = await provider.Create();
        await kept.DisposeAsync();
        await provider.Delete(gone.Id);
        await provider.Load(kept.Id);

        Assert.Equal([kept.Id], (await provider.List()).Select(info => info.Id));
    }

    [Fact]
    public async Task ASessionLoadedAfterBeingDisposedKeepsTheClockOfTheProvider()
    {
        ManualTime clock = new();
        FakePromptProvider provider = new() { Time = clock };
        IPromptSession session = await provider.Create();
        await session.DisposeAsync();
        FakePromptSession resumed = Assert.IsType<FakePromptSession>(await provider.Load(session.Id));
        resumed.Reply("fine");

        await resumed.Request("go");

        Assert.Equal(clock.GetUtcNow(), Assert.Single(resumed.Prompts).Time);
    }

    [Fact]
    public async Task DeleteRemovesTheSessionAndSaysWhetherItWasThere()
    {
        FakePromptProvider provider = new();
        IPromptSession session = await provider.Create();

        Assert.True(await provider.Delete(session.Id));
        Assert.False(await provider.Delete(session.Id));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => provider.Load(session.Id));
    }

    [Fact]
    public async Task LoadReturnsTheOpenSessionAndBringsBackOneThatWasDisposedWithItsState()
    {
        FakePromptProvider provider = new() { OnCreate = session => session.Reply("one").Reply("two") };
        IPromptSession session = await provider.Create();
        session.Configuration.Instructions = "kept";
        await session.Request("first");

        Assert.Same(session, await provider.Load(session.Id));
        await session.DisposeAsync();
        IPromptSession loaded = await provider.Load(session.Id);

        Assert.NotSame(session, loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal("kept", loaded.Configuration.Instructions);
        Assert.Equal(1, (await loaded.GetHistory().Take(10).ToListAsync()).Count(entry => entry.Event.GetType() == typeof(PromptInputEvent)));
        Assert.Equal("two", await loaded.Request("second"));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => provider.Load("missing"));
    }

    [Fact]
    public async Task TheSessionsShareTheWindowsOfTheProviderUnlessTheyHaveTheirOwn()
    {
        FakePromptProvider provider = new();
        provider.Windows = TestWindows.Claude(session: 0.95);
        FakePromptSession session = (FakePromptSession)await provider.Create();
        session.Hang();
        session.Configuration.Limits = session.Configuration.Limits with { WindowLimit = new PromptWindowLimit { Reached = status => status.Used("Session", 0.9) } };

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => session.Request("x"));

        Assert.Equal(PromptLimit.Window, exception.Limit);
        session.Windows = TestWindows.Claude(session: 0.1);
        Assert.Equal(0.1, session.Windows!.Single().Used);
    }

    [Fact]
    public async Task ASessionWithoutAnyWindowsLooksLikeALoginWithoutLimits()
    {
        FakePromptSession session = new FakePromptSession().Hang();
        session.Configuration.Limits = session.Configuration.Limits with { WindowLimit = new PromptWindowLimit { Reached = status => status.Used("Session", 0.9) } };

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("x"));

        Assert.Contains("no subscription limits", exception.Message);
    }

    [Fact]
    public async Task ACancelledCreateAndLoadThrow()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        FakePromptProvider provider = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Create(cancellation: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Load("x", cancellation: cancelled.Token));
    }

    [Fact]
    public async Task TestReportsAvailableUnlessToldOtherwiseAndRemembersTheKeys()
    {
        FakePromptProvider provider = new();

        PromptTestResult first = await provider.Test("key");
        provider.FailTests("not signed in");
        PromptTestResult second = await provider.Test();

        Assert.True(first.IsAvailable);
        Assert.False(second.IsAvailable);
        Assert.Equal("not signed in", second.Error);
        Assert.Equal(["key", null], provider.Tests);
        PromptTestResult custom = new() { IsAvailable = true };
        provider.TestResult = custom;
        Assert.Same(custom, await provider.Test());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.Test(null, cancelled.Token));
    }

    [Fact]
    public async Task GetWindowsReturnsTheWindowsItWasGivenAndOtherwiseLooksLikeALoginWithoutLimits()
    {
        FakePromptProvider provider = new();

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => provider.GetWindows());
        IReadOnlyList<PromptWindow> windows = TestWindows.Claude(session: 0.1);
        provider.Windows = windows;

        Assert.Contains("no subscription limits", exception.Message);
        Assert.Same(windows, await provider.GetWindows());
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetWindows(cancelled.Token));
    }
}
