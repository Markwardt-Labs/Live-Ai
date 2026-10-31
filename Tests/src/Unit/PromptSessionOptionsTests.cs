namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionOptionsTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<IPromptProvider> provider = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptProcess> process = new();

    private readonly Mock<IPromptToolHost> toolHost = new();

    private readonly Mock<IPromptLibraryTools> libraryTools = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    public PromptSessionOptionsTests()
    {
        libraryTools.SetupGet(mock => mock.Names).Returns(["read_file"]);
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.Plain(It.IsAny<PromptInput>())).Returns((PromptInput turn) => turn.Text);
        libraryTools.SetupGet(mock => mock.ShellName).Returns("shell");
        libraryTools.Setup(mock => mock.CreateShell(It.IsAny<string>())).Returns(() => Tool("shell"));
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns(() => [Tool("read_file")]);
        engine.Setup(mock => mock.CreateStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("tool"));
        engine.Setup(mock => mock.CreatePersistentStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("tool"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(() => Server().Object);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events());
        runner.Setup(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>())).Returns(process.Object);
        process.SetupGet(mock => mock.IsRunning).Returns(true);
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() => Events());
    }

    private PromptSessionRecord Record(Action<PromptSessionRecordBuilder>? change = null)
    {
        PromptSessionRecordBuilder builder = new();
        change?.Invoke(builder);
        return builder.Build(engine.Object);
    }

    private PromptSession Session(PromptSessionRecord? record = null, string? apiKey = null)
        => new(record ?? Record(), apiKey, store.Object, provider.Object, engine.Object, runner.Object, toolHost.Object, libraryTools.Object, opener.Object);

    private async IAsyncEnumerable<PromptEngineEvent> Events(params PromptEngineEvent[] events)
    {
        foreach (PromptEngineEvent output in events)
        {
            await Task.Yield();
            yield return output;
        }
    }

    private PromptEngineEvent Event(PromptEngineEventKind kind, string text = "")
        => new() { Kind = kind, Text = text };

    private PromptTool Tool(string name)
        => new() { Name = name, Description = name, InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement, Handler = (_, _) => Task.FromResult("ok") };

    private Mock<IPromptToolServer> Server()
    {
        Mock<IPromptToolServer> server = new();
        server.SetupGet(mock => mock.Endpoint).Returns(new ToolEndpoint { Url = "http://127.0.0.1:1/mcp", Token = "t", Names = [] });
        return server;
    }

    [Fact]
    public async Task Events_PassesToolCallsUsageAndWindowsThroughBetweenTheMessages()
    {
        PromptEvent call = new PromptToolCallEvent { Id = "1", Name = "x" };
        PromptEvent usage = new PromptUsageEvent { Usage = new PromptUsage { OutputTokens = 3 } };
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), "hi", It.IsAny<CancellationToken>())).Returns(Events(
            Event(PromptEngineEventKind.Session, "abc"),
            Event(PromptEngineEventKind.Reply, "one"),
            new PromptEngineEvent { Kind = PromptEngineEventKind.Event, Event = call },
            new PromptEngineEvent { Kind = PromptEngineEventKind.Event, Event = usage },
            Event(PromptEngineEventKind.Finished)));

        List<PromptEvent> events = await Session().EventList("hi");

        Assert.Equal<PromptEvent>([new PromptMessageEvent { Text = "one" }, call, usage], events);
    }

    [Fact]
    public void Configure_StoresInstructionsAndMcpServersWithTheSessionAndTreatsBlankInstructionsAsNone()
    {
        PromptSession session = Session();
        PromptMcpServer server = PromptMcpServer.Stdio("tool", "--flag");

        session.Configuration.Instructions = "Be brief.";
        session.Configuration.McpServers["db"] = server;

        Assert.Equal("Be brief.", session.Configuration.Instructions);
        Assert.Equal(server, session.Configuration.McpServers["db"]);
        session.Configuration.Instructions = "  ";
        Assert.Null(session.Configuration.Instructions);
    }

    [Fact]
    public async Task InstructionsAndMcpServers_ReachTheProviderAndAreNotStored()
    {
        PromptSession session = Session();
        session.Configuration.Instructions = "Be brief.";
        session.Configuration.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");

        await session.Request("go");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Instructions.EndsWith("Be brief.") && start.Servers.Any(server => server.Name == "db") && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("liveai")]
    [InlineData("bad name")]
    [InlineData("")]
    public void Configure_RejectsMcpServersWithBadNames(string name)
    {
        PromptSession session = Session();

        Assert.Throws<ArgumentException>(() => session.Configuration.McpServers[name] = PromptMcpServer.Http("https://example.com"));
        Assert.Empty(session.Configuration.McpServers);
    }

    [Fact]
    public void Configure_RejectsMcpServersThatAreNeitherACommandNorAnAddressNorBoth()
    {
        PromptSession session = Session();

        Assert.Throws<ArgumentException>(() => session.Configuration.McpServers["a"] = new PromptMcpServer());
        Assert.Throws<ArgumentException>(() => session.Configuration.McpServers["a"] = new PromptMcpServer { Command = "x", Url = "https://example.com" });
        Assert.Throws<ArgumentException>(() => session.Configuration.McpServers["a"] = PromptMcpServer.Http("not a url"));
        Assert.Throws<ArgumentException>(() => session.Configuration.McpServers["a"] = PromptMcpServer.Http("ftp://example.com"));
        Assert.Throws<ArgumentException>(() => session.Configuration.McpServers["a"] = PromptMcpServer.Stdio(""));
        Assert.Throws<ArgumentException>(() => session.Configuration.McpServers["a"] = PromptMcpServer.Stdio("  "));
        Assert.Empty(session.Configuration.McpServers);
    }

    [Fact]
    public async Task ChangingInstructionsOrServers_EndsAKeptProcessOnceItsPromptIsDone()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session();
        session.Configuration.IsPersistent = true;
        await session.Request("one");

        session.Configuration.Instructions = "New.";
        await session.Request("two");

        runner.Verify(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
    }

    [Fact]
    public async Task BeforeToolCall_DecidesEachCallAndIsReadWhenTheCallArrives()
    {
        PromptSession session = Session();
        IPromptToolHooks hooks = session;
        PromptToolCallEvent call = new() { Id = "call-1", Name = "shell", Input = """{"command":"ls"}""" };
        List<string> asked = [];

        Assert.True((await hooks.Before(call)).Allowed);
        session.Configuration.BeforeToolCall = (asking, _) =>
        {
            asked.Add(asking.Name);
            return Task.FromResult(PromptToolDecision.Deny("not now"));
        };
        PromptToolDecision decision = await hooks.Before(call);
        session.Configuration.BeforeToolCall = null;

        Assert.False(decision.Allowed);
        Assert.Equal("not now", decision.Reason);
        Assert.Equal(["shell"], asked);
        Assert.True((await hooks.Before(call)).Allowed);
    }

    [Fact]
    public async Task AfterToolCall_IsToldWhatBecameOfEachCall()
    {
        PromptSession session = Session();
        IPromptToolHooks hooks = session;
        PromptToolCallEvent call = new() { Id = "call-2", Name = "read_file" };
        PromptToolResultEvent outcome = new() { Id = "call-2", Output = "text", IsError = false };
        List<(PromptToolResultEvent Result, TimeSpan Duration)> told = [];

        await hooks.After(call, outcome, TimeSpan.FromSeconds(1));
        session.Configuration.AfterToolCall = (_, reported, took, _) =>
        {
            told.Add((reported, took));
            return Task.CompletedTask;
        };
        await hooks.After(call, outcome, TimeSpan.FromSeconds(1));

        Assert.Equal([(outcome, TimeSpan.FromSeconds(1))], told);
    }

    [Fact]
    public void ToolCallHooksAndTheWorkspaceStartEmptyAndCanBeSet()
    {
        PromptSession session = Session();
        IStore workspace = new Mock<IStore>().Object;
        Func<PromptToolCallEvent, CancellationToken, Task<PromptToolDecision>> before = (_, _) => Task.FromResult(PromptToolDecision.Allow);

        Assert.Equal((null, null, null), (session.Configuration.BeforeToolCall, session.Configuration.AfterToolCall, session.Configuration.Workspace));
        session.Configuration.BeforeToolCall = before;
        session.Configuration.Workspace = workspace;
        Assert.Same(before, session.Configuration.BeforeToolCall);
        Assert.Same(workspace, session.Configuration.Workspace);
    }

    [Fact]
    public async Task TheToolServerIsGivenTheSessionToTellItOfEveryCall()
    {
        PromptSession session = Session();
        session.Configuration.Tools.Add(Tool("mine"));

        await session.Request("go");

        toolHost.Verify(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), session), Times.Once);
    }

    [Fact]
    public async Task GetWindows_ReadsThroughTheEngine()
    {
        IReadOnlyList<PromptWindow> status = TestWindows.Claude(session: 0.05);
        engine.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(status);
        IPromptExecutor executor = Session();

        Assert.Same(status, await executor.GetWindows());
    }

    [Fact]
    public async Task TheFirstSessionEventOfABranch_ReplacesTheOriginalReferenceInTheStoredRecord()
    {
        PromptSession session = Session(Record(builder => builder.ForkedFrom = "origin"));
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Events(Event(PromptEngineEventKind.Session, "own")));

        await session.Request("go");

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(record => record.AiSessionId == "own" && record.ForkedFrom == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Fork_BranchesTheProviderSessionStoresTheCopyAndOpensIt()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "origin"), "key");
        engine.Setup(mock => mock.Branch(It.Is<PromptEngineStart>(start => start.AiSessionId == "origin"), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { AiSessionId = "copy" });
        Mock<IPromptSession> opened = new();
        PromptSessionLink? stored = null;
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), "key")).Returns((PromptSessionLink record, IPromptProvider _, IPromptEngine _, string? _) =>
        {
            stored = record;
            return opened.Object;
        });
        PromptTool tool = Tool("mine");
        IStore files = new Mock<IStore>().Object;
        session.Configuration.Tools.Add(tool);
        session.Configuration.Mounts["docs"] = files;
        PromptSessionSettings forkSettings = new(Record(), []);
        forkSettings.Mounts["stale"] = files;
        opened.SetupGet(mock => mock.Configuration).Returns(forkSettings);
        Func<PromptToolCallEvent, CancellationToken, Task<PromptToolDecision>> approval = (_, _) => Task.FromResult(PromptToolDecision.Allow);
        session.Configuration.BeforeToolCall = approval;
        session.Configuration.Limits = session.Configuration.Limits with { Budget = new PromptBudget { MaxTokens = 9 } };
        session.Configuration.Limits = session.Configuration.Limits with { Retry = new PromptRetry { MaxAttempts = 4, ShouldRetry = _ => false } };
        session.Configuration.Limits = session.Configuration.Limits with { MaxToolCalls = 3 };
        session.Configuration.Limits = session.Configuration.Limits with { Timeout = TimeSpan.FromMinutes(2) };

        IPromptSession fork = await session.Fork();

        Assert.Same(opened.Object, fork);
        Assert.NotEqual(session.Id, stored!.Id.ToString("N"));
        Assert.Equal("copy", stored.AiSessionId);
        Assert.Null(stored.ForkedFrom);
        Assert.NotEqual(Record().WorkingFolder, stored.WorkingFolder);
        Assert.True(Directory.Exists(stored.WorkingFolder));
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(record => record.Id == stored.Id), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([tool], forkSettings.Tools);
        Assert.Equal(["docs"], forkSettings.Mounts.Keys);
        Assert.Same(approval, forkSettings.BeforeToolCall);
        Assert.Equal(9, forkSettings.Limits.Budget!.MaxTokens);
        Assert.Equal(4, forkSettings.Limits.Retry!.MaxAttempts);
        Assert.NotNull(forkSettings.Limits.Retry.ShouldRetry);
        Assert.Equal(3, forkSettings.Limits.MaxToolCalls);
        Assert.Equal(TimeSpan.FromMinutes(2), forkSettings.Limits.Timeout);
        Directory.Delete(stored.WorkingFolder, true);
    }

    [Fact]
    public async Task Fork_WhenTheToolBranchesByItself_RemembersTheOriginalForTheFirstPrompt()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "origin"));
        engine.Setup(mock => mock.Branch(It.Is<PromptEngineStart>(start => start.AiSessionId == "origin"), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { ForkedFrom = "origin" });
        PromptSessionLink? stored = null;
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns((PromptSessionLink record, IPromptProvider _, IPromptEngine _, string? _) =>
        {
            stored = record;
            return OpenedSession();
        });

        await session.Fork();

        Assert.Null(stored!.AiSessionId);
        Assert.Equal("origin", stored.ForkedFrom);
        Directory.Delete(stored.WorkingFolder, true);
    }

    [Fact]
    public async Task Fork_WithoutAProviderSession_BranchesNothing()
    {
        PromptSession session = Session();
        PromptSessionLink? stored = null;
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns((PromptSessionLink record, IPromptProvider _, IPromptEngine _, string? _) =>
        {
            stored = record;
            return OpenedSession();
        });

        await session.Fork();

        engine.Verify(mock => mock.Branch(It.IsAny<PromptEngineStart>(), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(stored!.AiSessionId);
        Assert.Null(stored.ForkedFrom);
        Directory.Delete(stored.WorkingFolder, true);
    }

    [Fact]
    public async Task Fork_WaitsForThePromptThatIsRunning()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "origin"));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task));
        engine.Setup(mock => mock.Branch(It.Is<PromptEngineStart>(start => start.AiSessionId == "origin"), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { AiSessionId = "copy" });
        PromptSessionLink? stored = null;
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns((PromptSessionLink record, IPromptProvider _, IPromptEngine _, string? _) =>
        {
            stored = record;
            return OpenedSession();
        });
        Task<string> prompt = session.Request("go");
        await Task.Delay(100);

        Task<IPromptSession> forking = session.Fork();
        await Task.Delay(100);

        Assert.False(forking.IsCompleted);
        release.SetResult();
        await prompt;
        await forking;
        Directory.Delete(stored!.WorkingFolder, true);
    }

    [Fact]
    public async Task Fork_WhenTheCopyFailsWithAnIoError_FailsWithASessionError()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "origin"));
        engine.Setup(mock => mock.Branch(It.IsAny<PromptEngineStart>(), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Fork());

        Assert.Contains("disk full", exception.Message);
    }

    private PromptHistoryEntry Entry(string text, long position = 0, int index = 0)
        => new() { Event = new PromptInputEvent { Text = text }, Point = new PromptHistoryPoint(position, index) };

    private async IAsyncEnumerable<PromptHistoryEntry> Newest(params PromptHistoryEntry[] oldestFirst)
    {
        foreach (PromptHistoryEntry entry in oldestFirst.Reverse())
        {
            await Task.Yield();
            yield return entry;
        }
    }

    private PromptHistoryEntry[] ThreeEntries()
        => [Entry("one", 0), Entry("two", 10), Entry("three", 20)];

    [Fact]
    public async Task History_ReadsTheProvidersConversationOfTheSessionOrItsOriginalNewestFirst()
    {
        engine.Setup(mock => mock.ReadHistory("own", It.IsAny<CancellationToken>())).Returns(() => Newest(ThreeEntries()));
        engine.Setup(mock => mock.ReadHistory("origin", It.IsAny<CancellationToken>())).Returns(() => Newest(ThreeEntries()));

        Assert.Equal(["three", "two", "one"], (await Session(Record(builder => builder.AiSessionId = "own")).GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text));
        Assert.Equal(["three", "two", "one"], (await Session(Record(builder => builder.ForkedFrom = "origin")).GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text));
        Assert.Empty(await Session().GetHistory().Take(100).ToListAsync());
    }

    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(1, new[] { "three" })]
    [InlineData(2, new[] { "three", "two" })]
    [InlineData(3, new[] { "three", "two", "one" })]
    [InlineData(10, new[] { "three", "two", "one" })]
    public async Task History_WithACount_ReturnsOnlyTheNewestEntries(int count, string[] expected)
    {
        engine.Setup(mock => mock.ReadHistory("own", It.IsAny<CancellationToken>())).Returns(() => Newest(ThreeEntries()));

        Assert.Equal(expected, (await Session(Record(builder => builder.AiSessionId = "own")).GetHistory().Take(count).ToListAsync()).Select(entry => entry.Text));
    }

    [Fact]
    public async Task History_ReadsNoMoreOfTheConversationThanItNeeds()
    {
        int read = 0;
        engine.Setup(mock => mock.ReadHistory("own", It.IsAny<CancellationToken>())).Returns(() => Counted(ThreeEntries(), () => read++));

        await Session(Record(builder => builder.AiSessionId = "own")).GetHistory().Take(1).ToListAsync();

        Assert.Equal(1, read);
    }

    [Fact]
    public async Task History_Before_ContinuesWithTheEntriesOlderThanTheOneGiven()
    {
        engine.Setup(mock => mock.ReadHistory("own", It.IsAny<CancellationToken>())).Returns(() => Newest(ThreeEntries()));
        PromptSession session = Session(Record(builder => builder.AiSessionId = "own"));

        IReadOnlyList<PromptHistoryEntry> page = await session.GetHistory().Take(1).ToListAsync();
        IReadOnlyList<PromptHistoryEntry> next = await session.GetHistory().OlderThan(page[^1].Point).Take(1).ToListAsync();
        IReadOnlyList<PromptHistoryEntry> last = await session.GetHistory().OlderThan(next[^1].Point).Take(5).ToListAsync();

        Assert.Equal(["three"], page.Select(entry => entry.Text));
        Assert.Equal(["two"], next.Select(entry => entry.Text));
        Assert.Equal(["one"], last.Select(entry => entry.Text));
        Assert.Empty(await session.GetHistory().OlderThan(last[^1].Point).Take(5).ToListAsync());
    }

    [Fact]
    public async Task History_After_ReturnsOnlyTheEntriesNewerThanTheOneGivenAndReadsNoFurther()
    {
        int read = 0;
        engine.Setup(mock => mock.ReadHistory("own", It.IsAny<CancellationToken>())).Returns(() => Counted(ThreeEntries(), () => read++));
        PromptSession session = Session(Record(builder => builder.AiSessionId = "own"));

        IReadOnlyList<PromptHistoryEntry> news = await session.GetHistory().NewerThan(new PromptHistoryPoint(0)).Take(10).ToListAsync();

        Assert.Equal(["three", "two"], news.Select(entry => entry.Text));
        Assert.Equal(3, read);
        Assert.Empty(await session.GetHistory().NewerThan(new PromptHistoryPoint(20)).Take(10).ToListAsync());
    }

    [Fact]
    public async Task History_ComparesEntriesOfOneRecordByTheirIndex()
    {
        PromptHistoryEntry[] entries = [Entry("a", 5, 0), Entry("b", 5, 1), Entry("c", 5, 2)];
        engine.Setup(mock => mock.ReadHistory("own", It.IsAny<CancellationToken>())).Returns(() => Newest(entries));
        PromptSession session = Session(Record(builder => builder.AiSessionId = "own"));

        Assert.Equal(["a"], (await session.GetHistory().OlderThan(entries[1].Point).Take(10).ToListAsync()).Select(entry => entry.Text));
        Assert.Equal(["c"], (await session.GetHistory().NewerThan(entries[1].Point).Take(10).ToListAsync()).Select(entry => entry.Text));
    }

    [Fact]
    public async Task History_WhenTheFileCannotBeRead_FailsWithASessionError()
    {
        engine.Setup(mock => mock.ReadHistory("own", It.IsAny<CancellationToken>())).Returns(() => Broken());

        await Assert.ThrowsAsync<PromptSessionException>(() => Session(Record(builder => builder.AiSessionId = "own")).GetHistory().Take(100).ToListAsync().AsTask());
    }

    private async IAsyncEnumerable<PromptHistoryEntry> Counted(PromptHistoryEntry[] oldestFirst, Action onRead)
    {
        foreach (PromptHistoryEntry entry in oldestFirst.Reverse())
        {
            onRead();
            await Task.Yield();
            yield return entry;
        }
    }

    private async IAsyncEnumerable<PromptHistoryEntry> Broken()
    {
        await Task.Yield();
        throw new IOException("locked");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private PromptEngineEvent Usage(long input, long output, decimal? cost = null)
        => new() { Kind = PromptEngineEventKind.Event, Event = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = input, OutputTokens = output, CostUsd = cost } } };

    private void EveryPromptUses(long input, long output, decimal? cost = null)
        => runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "ok"), Usage(input, output, cost), Event(PromptEngineEventKind.Finished)));

    [Fact]
    public async Task Usage_AddsUpWhatEveryPromptUsed()
    {
        PromptSession session = Session();
        EveryPromptUses(100, 20, 0.25m);

        Assert.Equal(new PromptUsage(), session.Usage);
        await session.Request("one");
        await session.Request("two");

        Assert.Equal(new PromptUsage { InputTokens = 200, OutputTokens = 40, CostUsd = 0.5m }, session.Usage);
    }

    [Fact]
    public async Task Budget_OfTokens_StopsTheNextPromptOnceReachedAndNotBefore()
    {
        PromptSession session = Session();
        EveryPromptUses(100, 20);
        session.Configuration.Limits = session.Configuration.Limits with { Budget = new PromptBudget { MaxTokens = 200 } };

        await session.Request("one");
        await session.Request("two");
        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => session.Request("three"));

        Assert.Equal(PromptLimit.Budget, exception.Limit);
        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Budget_OfCost_StopsTheNextPromptAndNeverStopsAToolThatReportsNoCost()
    {
        PromptSession costly = Session();
        EveryPromptUses(1, 1, 0.6m);
        costly.Configuration.Limits = costly.Configuration.Limits with { Budget = new PromptBudget { MaxCostUsd = 1m } };
        await costly.Request("one");
        await costly.Request("two");
        await Assert.ThrowsAsync<PromptLimitException>(() => costly.Request("three"));

        PromptSession free = Session();
        EveryPromptUses(1, 1);
        free.Configuration.Limits = free.Configuration.Limits with { Budget = new PromptBudget { MaxCostUsd = 0.01m } };
        for (int prompt = 0; prompt < 5; prompt++)
        {
            await free.Request("go");
        }
    }

    [Fact]
    public async Task Budget_CanBeRaisedOrRemovedToGoOn()
    {
        PromptSession session = Session();
        EveryPromptUses(100, 0);
        session.Configuration.Limits = session.Configuration.Limits with { Budget = new PromptBudget { MaxTokens = 100 } };
        await session.Request("one");
        await Assert.ThrowsAsync<PromptLimitException>(() => session.Request("two"));

        session.Configuration.Limits = session.Configuration.Limits with { Budget = new PromptBudget { MaxTokens = 1000 } };
        await session.Request("three");
        session.Configuration.Limits = session.Configuration.Limits with { Budget = null };
        await session.Request("four");

        Assert.Null(session.Configuration.Limits.Budget);
    }

    [Fact]
    public void Budget_WithALimitThatIsNotPositive_IsRefused()
    {
        PromptSession session = Session();

        Assert.Throws<ArgumentException>(() => session.Configuration.Limits = session.Configuration.Limits with { Budget = new PromptBudget { MaxTokens = 0 } });
        Assert.Throws<ArgumentException>(() => session.Configuration.Limits = session.Configuration.Limits with { Budget = new PromptBudget { MaxCostUsd = -1m } });
        Assert.Null(session.Configuration.Limits.Budget);
    }

    [Fact]
    public async Task Timeout_AppliesToEveryRequestUntilItIsCleared()
    {
        PromptSession session = Session();
        RunsHang();
        session.Configuration.Limits = session.Configuration.Limits with { Timeout = TimeSpan.FromMilliseconds(80) };

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => session.Request("go"));
        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(300));
        session.Configuration.Limits = session.Configuration.Limits with { Timeout = null };
        Task<string> unlimited = session.Request("go", cancellation: stop.Token);
        await Task.Delay(150);
        bool unlimitedRunning = !unlimited.IsCompleted;

        Assert.Equal(PromptLimit.Timeout, exception.Limit);
        Assert.True(unlimitedRunning);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlimited);
    }

    [Fact]
    public async Task Limits_CarryRetriesToolCallLimitsAndWindowLimits()
    {
        PromptSession session = Session();
        runner.SetupSequence(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Failed, "rate limited")))
            .Returns(Events(Event(PromptEngineEventKind.Reply, "fine")))
            .Returns(Events(new PromptEngineEvent { Kind = PromptEngineEventKind.Event, Event = new PromptToolCallEvent { Id = "1", Name = "x" } }, new PromptEngineEvent { Kind = PromptEngineEventKind.Event, Event = new PromptToolCallEvent { Id = "2", Name = "x" } }));
        session.Configuration.Limits = session.Configuration.Limits with { Retry = new PromptRetry { MaxAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(1) } };
        session.Configuration.Limits = session.Configuration.Limits with { MaxToolCalls = 1 };

        Assert.Equal("fine", await session.Request("go"));
        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => session.Request("go"));

        Assert.Equal(PromptLimit.ToolCalls, exception.Limit);
        engine.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 0.99));
        RunsHang();
        session.Configuration.Limits = session.Configuration.Limits with { Retry = null };
        session.Configuration.Limits = session.Configuration.Limits with { MaxToolCalls = null };
        session.Configuration.Limits = session.Configuration.Limits with { WindowLimit = new PromptWindowLimit { Reached = status => status.Used("Session", 0.9), Interval = TimeSpan.FromSeconds(30) } };
        PromptLimitException status = await Assert.ThrowsAsync<PromptLimitException>(() => session.Request("go"));
        Assert.Equal(PromptLimit.Window, status.Limit);
    }

    [Fact]
    public void Defaults_WithValuesOutOfRange_AreRefused()
    {
        PromptSession session = Session();

        Assert.Throws<ArgumentException>(() => session.Configuration.Limits = session.Configuration.Limits with { Timeout = TimeSpan.Zero });
        Assert.Throws<ArgumentException>(() => session.Configuration.Limits = session.Configuration.Limits with { Timeout = TimeSpan.FromDays(40) });
        Assert.Throws<ArgumentException>(() => session.Configuration.Limits = session.Configuration.Limits with { WindowLimit = new PromptWindowLimit { Reached = _ => false, Interval = TimeSpan.Zero } });
        Assert.Throws<ArgumentException>(() => session.Configuration.Limits = session.Configuration.Limits with { Retry = new PromptRetry { MaxAttempts = 0 } });
        Assert.Throws<ArgumentException>(() => session.Configuration.Limits = session.Configuration.Limits with { MaxToolCalls = -1 });
        Assert.Null(session.Configuration.Limits.Timeout);
        Assert.Null(session.Configuration.Limits.WindowLimit);
        Assert.Null(session.Configuration.Limits.Retry);
        Assert.Null(session.Configuration.Limits.MaxToolCalls);
    }

    [Fact]
    public void Defaults_AreNotStored()
    {
        PromptSession session = Session();

        session.Configuration.Limits = session.Configuration.Limits with { Timeout = TimeSpan.FromMinutes(1) };
        session.Configuration.Limits = session.Configuration.Limits with { Budget = new PromptBudget { MaxTokens = 5 } };

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(TimeSpan.FromMinutes(1), session.Configuration.Limits.Timeout);
    }

    [Fact]
    public async Task Interrupt_WhenIdle_DoesNothing()
        => Assert.False(await Session().Interrupt());

    [Fact]
    public async Task Interrupt_EndsThePromptThatRunsNormallyWithAnInterruptedEventAndLeavesTheSessionUsable()
    {
        PromptSession session = Session();
        RunsHang();
        Task<List<PromptEvent>> running = session.EventList("long");
        await Task.Delay(150);

        Assert.True(await session.Interrupt());
        List<PromptEvent> events = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([new PromptInterruptedEvent()], events);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "again")));
        Assert.Equal("again", await session.Request("next"));
        Assert.False(await session.Interrupt());
    }

    [Fact]
    public async Task Interrupt_OfAKeptProcess_AsksItToStopAndSwallowsTheFailedTurnItEndsWith()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session();
        session.Configuration.IsPersistent = true;
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() => Interruptible(stopped.Task));
        process.Setup(mock => mock.Interrupt(It.IsAny<CancellationToken>())).Callback(() => stopped.SetResult()).Returns(Task.CompletedTask);
        Task<List<PromptEvent>> running = session.EventList("long");
        await Task.Delay(150);

        Assert.True(await session.Interrupt());
        List<PromptEvent> events = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal<PromptEvent>([new PromptMessageEvent { Text = "partial" }, new PromptInterruptedEvent()], events);
        process.Verify(mock => mock.Interrupt(It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(mock => mock.Stop(), Times.Never);
    }

    [Fact]
    public async Task ARequestAfterAnInterrupt_RunsInTheSameConversationOnceTheInterruptedPromptHasEnded()
    {
        PromptSession session = Session();
        int calls = 0;
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((IPromptEngine _, ProcessStartInfo _, string text, CancellationToken token) => Interlocked.Increment(ref calls) == 1 ? Hang(token) : Events(Event(PromptEngineEventKind.Reply, $"ran {text}")));
        Task<List<PromptEvent>> first = session.EventList("long");
        await Task.Delay(150);

        await session.Interrupt();
        Assert.Equal([new PromptInterruptedEvent()], await first.WaitAsync(TimeSpan.FromSeconds(5)));
        string second = await session.Request("new direction");

        Assert.Equal("ran new direction", second);
    }

    [Fact]
    public async Task Interrupt_AfterDisposal_Throws()
    {
        PromptSession session = Session();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Interrupt());
    }

    [Fact]
    public async Task Cancelling_StillThrowsAndIsNotTakenForAnInterruption()
    {
        PromptSession session = Session();
        RunsHang();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Request("long", null, cancellation: cancellation.Token));
    }

    private async IAsyncEnumerable<PromptEngineEvent> Interruptible(Task interrupted)
    {
        yield return Event(PromptEngineEventKind.Reply, "partial");
        await interrupted;
        yield return Event(PromptEngineEventKind.Failed, "aborted");
    }

    private IPromptSession OpenedSession()
    {
        Mock<IPromptSession> opened = new();
        opened.SetupGet(mock => mock.Configuration).Returns(new PromptSessionSettings(Record(), []));
        return opened.Object;
    }

    private async IAsyncEnumerable<PromptEngineEvent> Hang([EnumeratorCancellation] CancellationToken cancellation)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        yield break;
    }

    private void RunsHang()
        => runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((IPromptEngine _, ProcessStartInfo _, string _, CancellationToken token) => Hang(token));

    private async IAsyncEnumerable<PromptEngineEvent> Blocked(Task release)
    {
        await release;
        yield return Event(PromptEngineEventKind.Reply, "done");
    }

    private PromptHistoryEntry Entry(long position = 7)
        => new() { Event = new PromptInputEvent { Text = "asked" }, Point = new PromptHistoryPoint(position) };

    [Fact]
    public async Task Compact_RunsItOnAProcessOfItsOwnFollowsTheNewSessionAndEndsTheProcess()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session(Record(builder => builder.AiSessionId = "old"));
        process.Setup(mock => mock.Compact(It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Session, "new"), Event(PromptEngineEventKind.Finished)));

        await session.Compact();

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == "new" && link.ForkedFrom == null && link.ForkedAt == null), It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.Is<PromptEngineStart>(start => start.AiSessionId == "old")), Times.Once);
        process.Verify(mock => mock.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Compact_OfAPersistentSession_UsesTheKeptProcessAndLeavesItRunning()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session();
        session.Configuration.IsPersistent = true;
        process.Setup(mock => mock.Compact(It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Finished)));
        await session.Request("one");

        await session.Compact();
        await session.Request("two");

        runner.Verify(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Once);
        process.Verify(mock => mock.Compact(It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(mock => mock.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task Compact_WhenTheToolRefuses_FailsWithItsMessage()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session();
        process.Setup(mock => mock.Compact(It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Failed, "Not enough messages to compact")));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Compact());

        Assert.Equal("Not enough messages to compact", exception.Message);
        process.Verify(mock => mock.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task GetContext_AsksAProcessAndEndsItWhenTheSessionDoesNotKeepOne()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session();
        PromptContext context = new() { Tokens = 50, Limit = 200 };
        process.Setup(mock => mock.GetContext(It.IsAny<CancellationToken>())).ReturnsAsync(context);

        Assert.Same(context, await session.GetContext());

        process.Verify(mock => mock.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task CompactAndGetContext_OfAToolThatCannotKeepAProcess_AreNotSupported()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(false);
        PromptSession session = Session();

        await Assert.ThrowsAsync<NotSupportedException>(() => session.Compact());
        await Assert.ThrowsAsync<NotSupportedException>(() => session.GetContext());
    }

    [Fact]
    public async Task CompactAndGetContext_AfterDisposal_Throw()
    {
        PromptSession session = Session();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Compact());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetContext());
    }

    [Fact]
    public async Task Fork_AtAnEntry_AsksTheProviderToBranchThereAndStoresWhereTheBranchIs()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "origin"));
        PromptHistoryEntry entry = Entry();
        engine.Setup(mock => mock.Branch(It.IsAny<PromptEngineStart>(), entry.Point, It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { ForkedFrom = "origin", ForkedAt = "message" });
        Mock<IPromptSession> opened = new();
        opened.SetupGet(mock => mock.Configuration).Returns(new PromptSessionSettings(Record(), []));
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(opened.Object);

        IPromptSession fork = await session.Fork(entry);

        Assert.Same(opened.Object, fork);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == null && link.ForkedFrom == "origin" && link.ForkedAt == "message"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Fork_AtAnEntryOfASessionWithoutAConversation_Fails()
    {
        PromptSession session = Session();

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Fork(Entry()));
        engine.Verify(mock => mock.Branch(It.IsAny<PromptEngineStart>(), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rewind_ContinuesTheSameSessionInTheBranchStoresItAndEndsTheProcessThatHeldTheOldConversation()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session(Record(builder => builder.AiSessionId = "origin"));
        session.Configuration.IsPersistent = true;
        await session.Request("one");
        PromptHistoryEntry entry = Entry();
        engine.Setup(mock => mock.Branch(It.IsAny<PromptEngineStart>(), entry.Point, It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { AiSessionId = "earlier" });

        await session.Rewind(entry);
        await session.Request("two");

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == "earlier" && link.ForkedFrom == null), It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.Is<PromptEngineStart>(start => start.AiSessionId == "earlier")), Times.Once);
        process.Verify(mock => mock.Stop(), Times.Once);
    }

    [Fact]
    public async Task AttachTo_ContinuesTheGivenProviderSessionStoresItAndEndsTheProcessThatHeldTheOldOne()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session(Record(builder => { builder.AiSessionId = "old"; builder.ForkedFrom = null; }));
        session.Configuration.IsPersistent = true;
        engine.Setup(mock => mock.HasSession("other")).Returns(true);
        await session.Request("one");

        await session.AttachTo("other");
        await session.Request("two");

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == "other" && link.ForkedFrom == null && link.ForkedAt == null), It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.Is<PromptEngineStart>(start => start.AiSessionId == "other")), Times.Once);
        process.Verify(mock => mock.Stop(), Times.Once);
    }

    [Fact]
    public async Task AttachTo_AWorkspacePath_StoresItAsTheWorkingFolderSetsTheWorkspaceAndStartsTheNextProcessThere()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        using TempFolder folder = new();
        PromptSession session = Session(Record(builder => builder.AiSessionId = "old"));
        session.Configuration.IsPersistent = true;
        await session.Request("one");

        await session.AttachTo(null, new PromptSessionOptions { WorkspacePath = folder.Path, Sandbox = false });
        await session.Request("two");

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == null && link.WorkingFolder == folder.Path && link.WorkspacePath == folder.Path && !link.Sandbox), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(folder.Path, Assert.IsAssignableFrom<IDiskStore>(session.Configuration.Workspace).Path);
        runner.Verify(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.Is<PromptEngineStart>(start => start.WorkingFolder == folder.Path && start.AiSessionId == null)), Times.Once);
        process.Verify(mock => mock.Stop(), Times.Once);
    }

    [Fact]
    public async Task AttachTo_NoWorkspace_GoesBackToAnEmptyFolderOfTheSessionAndClearsTheWorkspace()
    {
        using TempFolder folder = new();
        PromptSessionRecord record = Record(builder => builder.AiSessionId = "old") with { WorkingFolder = folder.Path, WorkspacePath = folder.Path };
        PromptSession session = Session(record);
        session.Configuration.Workspace = IStore.FromDisk(folder.Path);
        string scratch = Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{record.Id:N}");

        try
        {
            await session.AttachTo();

            store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == null && link.WorkingFolder == scratch && link.WorkspacePath == null), It.IsAny<CancellationToken>()), Times.Once);
            Assert.True(Directory.Exists(scratch));
            Assert.Null(session.Configuration.Workspace);
        }
        finally
        {
            Directory.Delete(scratch);
        }
    }

    [Fact]
    public async Task AttachTo_WithAnApiKey_IsRefusedBecauseTheSessionKeepsItsKey()
    {
        PromptSession session = Session();

        await Assert.ThrowsAsync<ArgumentException>(() => session.AttachTo("other", new PromptSessionOptions { ApiKey = "new-key" }));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AttachTo_AWorkspaceThatDoesNotExist_FailsAndChangesNothing()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "old"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => session.AttachTo("old", new PromptSessionOptions { WorkspacePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}") }));

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(session.Configuration.Workspace);
    }

    [Fact]
    public async Task AttachTo_ALazyBranch_DropsItsOriginAndPoint()
    {
        PromptSession session = Session(Record(builder => { builder.ForkedFrom = "origin"; builder.ForkedAt = "message"; }));
        engine.Setup(mock => mock.HasSession("other")).Returns(true);

        await session.AttachTo("other");

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == "other" && link.ForkedFrom == null && link.ForkedAt == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AttachTo_ASessionTheProviderDoesNotHave_FailsAndChangesNothing()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "old"));

        PromptSessionNotFoundException exception = await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => session.AttachTo("nothing"));

        Assert.Equal("nothing", exception.Id);
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AttachTo_ABlankIdentifierOrADisposedSession_Fails(string id)
    {
        PromptSession session = Session();
        engine.Setup(mock => mock.HasSession("other")).Returns(true);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => session.AttachTo(id));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AttachTo("other"));
    }

    [Fact]
    public async Task Rewind_ToAPointOfALazyBranch_StoresTheOriginAndThePoint()
    {
        PromptSession session = Session(Record(builder => builder.AiSessionId = "origin"));
        engine.Setup(mock => mock.Branch(It.IsAny<PromptEngineStart>(), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { ForkedFrom = "origin", ForkedAt = "message" });

        await session.Rewind(Entry());

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.AiSessionId == null && link.ForkedFrom == "origin" && link.ForkedAt == "message"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Rewind_OfASessionWithoutAConversationOrAfterDisposal_Fails()
    {
        PromptSession session = Session();

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Rewind(Entry()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.Rewind(null!));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Rewind(Entry()));
    }

    [Fact]
    public async Task RetryWhen_DecidesWhichFailuresAreRetriedInsteadOfTheUsualRule()
    {
        PromptSession session = Session();
        runner.SetupSequence(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Failed, "never mind")))
            .Returns(Events(Event(PromptEngineEventKind.Failed, "try again")))
            .Returns(Events(Event(PromptEngineEventKind.Reply, "fine")));
        session.Configuration.Limits = session.Configuration.Limits with { Retry = new PromptRetry { MaxAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(1), ShouldRetry = exception => exception.Message == "try again" } };

        PromptSessionException refused = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));
        Assert.Equal("never mind", refused.Message);

        Assert.Equal("fine", await session.Request("go"));
    }

    [Fact]
    public async Task OneAttempt_IsNoRetryAtAll()
    {
        PromptSession session = Session();
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Failed, "rate limited")));
        session.Configuration.Limits = session.Configuration.Limits with { Retry = new PromptRetry { MaxAttempts = 1, InitialDelay = TimeSpan.FromMilliseconds(1) } };

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
