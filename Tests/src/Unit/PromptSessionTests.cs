namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<IPromptProvider> provider = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptProcess> process = new();

    private readonly Mock<IPromptToolHost> toolHost = new();

    private readonly Mock<IPromptLibraryTools> libraryTools = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private PromptSessionRecord Record(string? aiSessionId = null)
        => new PromptSessionRecordBuilder { AiSessionId = aiSessionId }.Build(engine.Object);

    public PromptSessionTests()
    {
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        libraryTools.SetupGet(mock => mock.Names).Returns(["read_file"]);
        engine.Setup(mock => mock.Plain(It.IsAny<PromptInput>())).Returns((PromptInput turn) => turn.Text);
        libraryTools.SetupGet(mock => mock.ShellName).Returns("shell");
        libraryTools.OfferTheEverydayTools();
        libraryTools.Setup(mock => mock.CreateShell(It.IsAny<string>())).Returns(() => Tool("shell"));
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns([]);
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(() => Server().Object);
    }

    private PromptSession Session(PromptSessionRecord record, string? apiKey = null)
    {
        engine.Setup(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == apiKey))).Returns(new ProcessStartInfo("tool"));
        return new PromptSession(record, apiKey, store.Object, provider.Object, engine.Object, runner.Object, toolHost.Object, libraryTools.Object, opener.Object);
    }

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

    [Fact]
    public void Properties_DescribeTheRecord()
    {
        PromptSessionRecord record = Record("abc");
        PromptSession session = Session(record, "key");

        Assert.Equal(record.Id.ToString("N"), session.Id);
        Assert.Same(provider.Object, session.Provider);
        Assert.Equal("sonnet", session.Configuration.Model!.Id);
        Assert.Equal("medium", session.Configuration.ModelSettings["effort"]);
    }

    [Fact]
    public async Task Send_ReportsEachReplyAndStoresTheNewProviderSession()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "hi", It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Session, "abc"), Event(PromptEngineEventKind.Reply, "one"), Event(PromptEngineEventKind.Reply, "two"), Event(PromptEngineEventKind.Finished)));

        List<string> replies = await session.Messages("hi");

        Assert.Equal(["one", "two"], replies);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Id.ToString("N") == session.Id && saved.AiSessionId == "abc"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_DoesNotStoreAProviderSessionThatIsAlreadyKnown()
    {
        PromptSession session = Session(Record("abc"));
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), "hi", It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Session, "abc"), Event(PromptEngineEventKind.Finished)));

        await session.Messages("hi");

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_StartsTheProcessFromTheCurrentRecord()
    {
        PromptSession session = Session(Record());
        runner.SetupSequence(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Session, "abc")))
            .Returns(Events());

        await session.Messages("one");
        await session.Messages("two");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.AiSessionId == null && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.AiSessionId == "abc" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
    }

    [Fact]
    public async Task Send_ThrowsWhenTheAiFails()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Reply, "partial"), Event(PromptEngineEventKind.Failed, "broken")));

        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);
        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("hi"));
        await recorder.Until(values => values.OfType<PromptFailureEvent>().Any());
        List<string> replies = [.. recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text)];

        Assert.Equal("broken", exception.Message);
        Assert.Equal(["partial"], replies);
    }

    [Fact]
    public async Task ADirectRequest_WhileAnotherIsRunning_ThrowsAStateExceptionAndLeavesTheFirstAlone()
    {
        PromptSession session = Session(Record());
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Blocked(release.Task, () => Interlocked.Increment(ref started)));

        Task<List<string>> first = session.Messages("one");
        while (Volatile.Read(ref started) == 0 && !first.IsCompleted)
        {
            await Task.Delay(10);
        }

        PromptSessionStateException exception = await Assert.ThrowsAsync<PromptSessionStateException>(() => session.Messages("two"));

        Assert.Equal(PromptSessionState.Responding, exception.State);
        Assert.Equal(PromptSessionState.Responding, session.State);
        release.SetResult();
        await first;
        Assert.Equal(1, started);
        Assert.Equal(PromptSessionState.Waiting, session.State);
    }

    [Fact]
    public void Settings_CanBeChangedAndReadBack()
    {
        PromptSession session = Session(Record());

        session.Configuration.Model = new PromptModel { Id = "opus", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium"])] };
        session.Configuration.ModelSettings["effort"] = "low";

        Assert.Equal("opus", session.Configuration.Model!.Id);
        Assert.Equal("low", session.Configuration.ModelSettings["effort"]);
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Settings_RejectValuesTheModelDoesNotAccept()
    {
        PromptSession session = Session(Record());

        Assert.Throws<ArgumentException>(() => session.Configuration.ModelSettings["effort"] = "extreme");
        Assert.Throws<ArgumentException>(() => session.Configuration.ModelSettings["unknown"] = "x");
        Assert.Equal("medium", session.Configuration.ModelSettings["effort"]);
    }

    [Fact]
    public async Task Settings_ChangedWhileAPromptRuns_ApplyFromTheNextPrompt()
    {
        PromptSession session = Session(Record());
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => { }));
        Task<List<string>> first = session.Messages("one");
        await Task.Delay(100);

        session.Configuration.ModelSettings["effort"] = "max";
        release.SetResult();
        await first;
        await session.Messages("two");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Settings["effort"] == "medium" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Settings["effort"] == "max" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
    }

    private PromptSession Persistent(PromptSessionRecord? record = null)
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        engine.Setup(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null))).Returns(new ProcessStartInfo("tool"));
        runner.Setup(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>())).Returns(process.Object);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "once")));
        process.SetupGet(mock => mock.IsRunning).Returns(true);
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "kept"), Event(PromptEngineEventKind.Finished)));
        PromptSession session = Session(record ?? Record());
        session.Configuration.IsPersistent = true;
        return session;
    }

    private async Task Idle(PromptSession session)
    {
        for (int attempt = 0; attempt < 500 && session.State != PromptSessionState.Waiting; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(PromptSessionState.Waiting, session.State);
    }

    private async Task Eventually(Action check)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                check();
                return;
            }
            catch (MockException)
            {
                await Task.Delay(20);
            }
        }

        check();
    }

    [Fact]
    public void IsPersistent_IsOffUntilSet()
    {
        PromptSession session = Session(Record());

        Assert.False(session.Configuration.IsPersistent);
        session.Configuration.IsPersistent = true;
        Assert.True(session.Configuration.IsPersistent);
    }

    [Fact]
    public async Task Send_WhenPersistent_KeepsOneProcessForEveryPrompt()
    {
        PromptSession session = Persistent();

        Assert.Equal(["kept"], await session.Messages("one"));
        Assert.Equal(["kept"], await session.Messages("two"));

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Once);
        process.Verify(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "one"), It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "two"), It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(mock => mock.Stop(), Times.Never);
        process.Verify(mock => mock.DisposeAsync(), Times.Never);
        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_WhenPersistent_StartsTheProcessFromTheCurrentRecord()
    {
        PromptSession session = Persistent(Record("abc"));

        await session.Messages("one");

        engine.Verify(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.AiSessionId == "abc" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("servers")]
    public async Task Send_WhenPersistentAndASettingTheToolIsStartedWithChanged_StartsANewProcess(string setting)
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        switch (setting)
        {
            case "instructions":
                session.Configuration.Instructions = "Be brief.";
                break;
            default:
                session.Configuration.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");
                break;
        }

        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
        process.Verify(mock => mock.Stop(), Times.Once);
        process.Verify(mock => mock.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Send_WhenPersistentAndTheModelOrTheEffortChanged_KeepsTheProcessAndGivesTheChangeToItWithThePrompt()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.Model = new PromptModel { Id = "opus", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "max"])] };
        session.Configuration.ModelSettings["effort"] = "max";
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Once);
        process.Verify(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "one" && turn.Model == "sonnet" && turn.Settings["effort"] == "medium"), It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "two" && turn.Model == "opus" && turn.Settings["effort"] == "max"), It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(mock => mock.Stop(), Times.Never);
        process.Verify(mock => mock.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task Configure_AppliesSeveralChangesTogetherAndRestartsTheProcessOnce()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.Instructions = "Be brief.";
        session.Configuration.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");
        session.Configuration.IsPersistent = true;
        await Eventually(() => process.Verify(mock => mock.Stop(), Times.Once));
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
        process.Verify(mock => mock.Stop(), Times.Once);
        process.Verify(mock => mock.DisposeAsync(), Times.Once);
        Assert.Equal("Be brief.", session.Configuration.Instructions);
    }

    [Fact]
    public async Task Configure_WithNothingChanged_KeepsTheProcess()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.Model = session.Configuration.Model;
        session.Configuration.IsPersistent = true;
        await Task.Delay(100);
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Once);
        process.Verify(mock => mock.Stop(), Times.Never);
        process.Verify(mock => mock.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task Configure_EndsAStaleProcessWithoutWaitingForTheNextPrompt()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.Instructions = "Be brief.";

        await Eventually(() => process.Verify(mock => mock.DisposeAsync(), Times.Once));
        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Once);
    }

    [Fact]
    public async Task Send_WhenPersistentAndTheProcessEnded_StartsANewProcess()
    {
        PromptSession session = Persistent();
        await session.Messages("one");
        process.SetupGet(mock => mock.IsRunning).Returns(false);

        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Send_WhenPersistentButTheToolCannotStayAlive_StartsAProcessPerPrompt()
    {
        PromptSession session = Persistent();
        engine.SetupGet(mock => mock.CanPersist).Returns(false);

        Assert.Equal(["once"], await session.Messages("one"));

        runner.Verify(mock => mock.Open(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Never);
    }

    [Fact]
    public async Task IsPersistent_WhenSetToFalse_EndsTheProcessInTheBackground()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.IsPersistent = false;

        await Eventually(() => process.Verify(mock => mock.DisposeAsync(), Times.Once));
        process.Verify(mock => mock.Stop(), Times.Never);
        Assert.Equal(["kept"], await session.Messages("two"));
        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
    }

    [Fact]
    public async Task IsPersistent_WhenSetToFalseWhileAPromptRuns_EndsTheProcessAfterThePrompt()
    {
        PromptSession session = Persistent();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Setup(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "slow"), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => { }));
        Task<List<string>> running = session.Messages("slow");
        await Task.Delay(100);

        session.Configuration.IsPersistent = false;
        await Task.Delay(100);
        process.Verify(mock => mock.DisposeAsync(), Times.Never);

        release.SetResult();
        await running;
        await Eventually(() => process.Verify(mock => mock.DisposeAsync(), Times.Once));
    }

    [Fact]
    public async Task IsPersistent_WhenSetToFalseAndBackOnBeforeItIsEnded_KeepsTheProcess()
    {
        PromptSession session = Persistent();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Setup(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "slow"), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => { }));
        Task<List<string>> running = session.Messages("slow");
        await Task.Delay(100);

        session.Configuration.IsPersistent = false;
        session.Configuration.IsPersistent = true;
        release.SetResult();
        await running;
        await Task.Delay(100);

        process.Verify(mock => mock.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_EndsTheProcessGracefullyAndRefusesFurtherPrompts()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        await session.DisposeAsync();
        await session.DisposeAsync();
        session.Dispose();

        process.Verify(mock => mock.Stop(), Times.Never);
        process.Verify(mock => mock.DisposeAsync(), Times.Once);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Messages("two"));
    }

    [Fact]
    public async Task Dispose_EndsTheProcessAtOnceAndRefusesFurtherPrompts()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Dispose();
        session.Dispose();

        process.Verify(mock => mock.Stop(), Times.AtLeastOnce);
        await Eventually(() => process.Verify(mock => mock.DisposeAsync(), Times.Once));
        await session.DisposeAsync();
        process.Verify(mock => mock.DisposeAsync(), Times.Once);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Messages("two"));
    }

    [Fact]
    public async Task Dispose_CancelsThePromptThatIsRunning()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((IPromptEngine _, ProcessStartInfo _, string _, CancellationToken token) => UntilCancelled(token));
        Task<List<string>> running = session.Messages("slow");
        await Task.Delay(100);

        session.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WaitsForThePromptThatIsRunning()
    {
        PromptSession session = Persistent();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Setup(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "slow"), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => { }));
        Task<List<string>> running = session.Messages("slow");
        await Task.Delay(100);
        Task<List<string>> asked = session.Messages("behind");
        await Assert.ThrowsAsync<PromptSessionStateException>(() => asked);
        await Task.Delay(50);

        ValueTask disposing = session.DisposeAsync();
        await Task.Delay(150);
        Assert.False(disposing.IsCompleted);
        release.SetResult();
        await disposing;

        Assert.Equal(["done"], await running);
        process.Verify(mock => mock.Stop(), Times.Never);
        process.Verify(mock => mock.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Dispose_WhileAGracefulDisposalWaits_HurriesItUp()
    {
        PromptSession session = Persistent();
        process.Setup(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "slow"), It.IsAny<CancellationToken>())).Returns((PromptInput _, CancellationToken token) => UntilCancelled(token));
        Task<List<string>> running = session.Messages("slow");
        await Task.Delay(100);
        ValueTask disposing = session.DisposeAsync();
        await Task.Delay(100);
        Assert.False(disposing.IsCompleted);

        session.Dispose();

        await disposing;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        process.Verify(mock => mock.Stop(), Times.AtLeastOnce);
    }

    private JsonElement Schema(string json = """{"type":"object"}""")
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Send_WithASchema_StartsTheProcessWithTheFormattedSchema()
    {
        PromptSession session = Session(Record());
        engine.Setup(mock => mock.FormatSchema(It.IsAny<JsonElement>())).Returns("""{"formatted":true}""");
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "{}")));

        Assert.Equal(["{}"], await session.Messages("hi", new PromptRequestOptions { Schema = Schema() }));

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema != null && start.Schema.Value.GetRawText() == """{"formatted":true}""" && start.OffersOnlyRespond())), Times.Once);
    }

    [Fact]
    public async Task Send_WithoutASchema_StartsTheProcessWithoutOne()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events());

        await session.Messages("hi");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
        engine.Verify(mock => mock.FormatSchema(It.IsAny<JsonElement>()), Times.Never);
    }

    [Theory]
    [InlineData("""{"type":"array"}""")]
    [InlineData("""{"type":["object","null"]}""")]
    [InlineData("""{"properties":{}}""")]
    [InlineData("""[]""")]
    [InlineData("\"text\"")]
    public async Task Send_WithASchemaThatIsNotAnObject_ThrowsBeforeStartingAnything(string json)
    {
        PromptSession session = Session(Record());

        await Assert.ThrowsAsync<ArgumentException>(() => session.Messages("hi", new PromptRequestOptions { Schema = Schema(json) }));

        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_WithTheDefaultSchema_Throws()
    {
        PromptSession session = Session(Record());

        await Assert.ThrowsAsync<ArgumentException>(() => session.Messages("hi", new PromptRequestOptions { Schema = default(JsonElement) }));
    }

    [Fact]
    public async Task Send_WhenPersistent_KeepsTheProcessWhileTheSchemaStaysTheSame()
    {
        PromptSession session = Persistent();
        engine.Setup(mock => mock.FormatSchema(It.IsAny<JsonElement>())).Returns((JsonElement schema) => schema.GetRawText());

        await session.Messages("one", new PromptRequestOptions { Schema = Schema("""{"type":"object","a":1}""") });
        await session.Messages("two", new PromptRequestOptions { Schema = Schema("""{"type":"object","a":1}""") });

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Once);
        engine.Verify(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema != null && start.Schema.Value.GetRawText() == """{"type":"object","a":1}""" && start.OffersOnlyRespond())), Times.Once);
    }

    [Fact]
    public async Task Send_WhenPersistentAndTheSchemaChanges_StartsANewProcess()
    {
        PromptSession session = Persistent();
        engine.Setup(mock => mock.FormatSchema(It.IsAny<JsonElement>())).Returns((JsonElement schema) => schema.GetRawText());

        await session.Messages("one", new PromptRequestOptions { Schema = Schema("""{"type":"object","a":1}""") });
        await session.Messages("two", new PromptRequestOptions { Schema = Schema("""{"type":"object","a":2}""") });
        await session.Messages("three");
        await session.Messages("four");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(3));
        process.Verify(mock => mock.Stop(), Times.Exactly(2));
        engine.Verify(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Exactly(1));
    }

    private void SetTools(PromptSession session, params PromptTool[] tools)
    {
        session.Configuration.Tools.Clear();

        foreach (PromptTool tool in tools)
        {
            session.Configuration.Tools.Add(tool);
        }
    }

    private PromptTool Tool(string name)
        => new() { Name = name, Description = name, InputSchema = Schema(), Handler = (_, _) => Task.FromResult("ok") };

    private Mock<IPromptToolServer> Server(string url = "http://127.0.0.1:1/mcp")
    {
        Mock<IPromptToolServer> server = new();
        server.SetupGet(mock => mock.Endpoint).Returns(new ToolEndpoint { Url = url, Token = "t", Names = [] });
        return server;
    }

    [Fact]
    public async Task Prompt_TakesAStringAsTextAndAnyOtherObjectAsJson()
    {
        PromptSession session = Session(Record());
        Plain();

        await session.Request("plain text");
        await session.Request((object)"typed as object");
        await session.Request(new Question("rate", [], Level.High));

        runner.Verify(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "plain text", It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "typed as object", It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), """{"task":"rate","items":[],"level":"High"}""", It.IsAny<CancellationToken>()), Times.Once);
    }

    private void Plain()
        => runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events());

    [Fact]
    public void Tools_AreEmptyUntilSet()
    {
        PromptSession session = Session(Record());
        PromptTool tool = Tool("alpha");

        Assert.Empty(session.Configuration.Tools);
        SetTools(session, tool);
        Assert.Equal([tool], session.Configuration.Tools);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dot.ted")]
    [InlineData("ünï")]
    public void Tools_RejectAnInvalidName(string name)
        => Assert.Throws<ArgumentException>(() => Session(Record()).Configuration.Tools.Add(Tool(name)));

    [Fact]
    public void Tools_RejectATooLongName()
        => Assert.Throws<ArgumentException>(() => Session(Record()).Configuration.Tools.Add(Tool(new string('a', 65))));

    [Fact]
    public void Tools_RejectDuplicateNames()
    {
        PromptSession session = Session(Record());
        SetTools(session, Tool("alpha"));

        Assert.Throws<ArgumentException>(() => SetTools(session, Tool("beta"), Tool("beta")));
        Assert.Single(session.Configuration.Tools);
    }

    [Fact]
    public async Task Send_WithoutTools_ServesOnlyTheRespondTool()
    {
        PromptSession session = Session(Record());
        Plain();

        await session.Messages("hi");

        Served();
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
    }

    [Fact]
    public async Task Send_WithTools_OffersThemThroughAServerThatLastsAcrossPrompts()
    {
        PromptSession session = Session(new PromptSessionRecordBuilder().Build(engine.Object));
        Mock<IPromptToolServer> server = Server();
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(server.Object);
        Plain();
        SetTools(session, Tool("alpha"), Tool("beta"));

        await session.Messages("one");
        await session.Messages("two");

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(new[] { "alpha", "beta", "respond" })), It.IsAny<IPromptToolHooks?>()), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.Reaches(server.Object.Endpoint))), Times.Exactly(2));
        server.Verify(mock => mock.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task Send_WhenTheToolsChange_ReplacesTheServer()
    {
        PromptSession session = Session(new PromptSessionRecordBuilder().Build(engine.Object));
        Mock<IPromptToolServer> first = Server("http://127.0.0.1:1/mcp");
        Mock<IPromptToolServer> second = Server("http://127.0.0.1:2/mcp");
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(first.Object).Returns(second.Object);
        Plain();
        SetTools(session, Tool("alpha"));
        await session.Messages("one");

        SetTools(session, Tool("beta"));
        await session.Messages("two");

        first.Verify(mock => mock.DisposeAsync(), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.Reaches(second.Object.Endpoint))), Times.Once);
    }

    [Fact]
    public async Task Send_WhenTheToolsAreRemoved_ReplacesTheServerWithOneThatOffersOnlyTheRespondTool()
    {
        PromptSession session = Session(new PromptSessionRecordBuilder().Build(engine.Object));
        Mock<IPromptToolServer> server = Server();
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(server.Object);
        Plain();
        SetTools(session, Tool("alpha"));
        await session.Messages("one");

        SetTools(session);
        await session.Messages("two");

        server.Verify(mock => mock.DisposeAsync(), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Exactly(2));
    }

    [Fact]
    public async Task DisposeAsync_StopsTheToolServer()
    {
        PromptSession session = Session(new PromptSessionRecordBuilder().Build(engine.Object));
        Mock<IPromptToolServer> server = Server();
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(server.Object);
        Plain();
        SetTools(session, Tool("alpha"));
        await session.Messages("one");

        await session.DisposeAsync();

        server.Verify(mock => mock.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Send_WhenPersistent_KeepsTheProcessWhileTheToolsStayTheSameAndRestartsItWhenTheyChange()
    {
        PromptSession session = Persistent(new PromptSessionRecordBuilder().Build(engine.Object));
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server("http://127.0.0.1:1/mcp").Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        SetTools(session, Tool("alpha"));

        await session.Messages("one");
        await session.Messages("two");
        SetTools(session, Tool("alpha"), Tool("beta"));
        await session.Messages("three");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
        engine.Verify(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.Servers[0].Url!.EndsWith(":2/mcp"))), Times.Once);
    }

    private PromptSession LibrarySession(bool mount = true)
    {
        PromptSession session = Session(new PromptSessionRecordBuilder().Build(engine.Object));

        if (mount)
        {
            session.Configuration.Mounts["main"] = new Mock<IStore>().Object;
        }

        return session;
    }

    private void Library(params PromptTool[] built)
        => libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns(built);

    private readonly string[] everyday = ["current_time", "tool_search", "delegate"];

    private void Served(params string[] names)
        => toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(names.Append("respond"))), It.IsAny<IPromptToolHooks?>()), Times.Once);

    [Fact]
    public void Mounts_AreEmptyUntilSetAndAreCopied()
    {
        PromptSession session = Session(Record());
        Dictionary<string, IStore> given = new() { ["docs"] = new Mock<IStore>().Object };

        Assert.Empty(session.Configuration.Mounts);
        foreach ((string name, IStore files) in given)
        {
            session.Configuration.Mounts[name] = files;
        }
        given["more"] = new Mock<IStore>().Object;

        Assert.Equal(["docs"], session.Configuration.Mounts.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("../a")]
    [InlineData("a\\b")]
    public void Mounts_RejectAnInvalidName(string name)
        => Assert.Throws<ArgumentException>(() => Session(Record()).Configuration.Mounts[name] = new Mock<IStore>().Object);

    [Fact]
    public async Task Mounts_AreAddedToAndRemovedFromOneAtATimeAcrossConfigureCalls()
    {
        PromptSession session = Session(Record());
        IStore first = new Mock<IStore>().Object;
        IStore second = new Mock<IStore>().Object;

        session.Configuration.Mounts["a"] = first;
        session.Configuration.Mounts["b"] = second;
        Assert.Equal(["a", "b"], session.Configuration.Mounts.Keys.Order());
        session.Configuration.Mounts.Remove("a");

        Assert.Equal(["b"], session.Configuration.Mounts.Keys);
        Assert.Same(second, session.Configuration.Mounts["b"]);
    }

    [Fact]
    public void Tools_AreAddedToAndRemovedFromOneAtATimeAcrossConfigureCalls()
    {
        PromptSession session = Session(Record());
        PromptTool alpha = Tool("alpha");

        session.Configuration.Tools.Add(alpha);
        session.Configuration.Tools.Add(Tool("beta"));
        session.Configuration.Tools.Remove(alpha);

        Assert.Equal(["beta"], session.Configuration.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task Tools_DoNotListTheLibraryFileToolsWhichAreServedInTheBackground()
    {
        PromptSession session = LibrarySession();
        Library(Tool("read_file"), Tool("write_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        SetTools(session, Tool("mine"));

        await session.Messages("one");

        Assert.Equal(["mine"], session.Configuration.Tools.Select(tool => tool.Name));
        Assert.Equal(["main"], session.Configuration.Mounts.Keys);
        Served("read_file", "write_file", "mine");
    }

    private PromptAttachment Image(string name = "a.png", params byte[] content)
        => new() { Name = name, MediaType = "image/png", Content = content.Length == 0 ? new byte[] { 1, 2, 3 } : content };

    private void Accepting()
    {
        engine.Setup(mock => mock.Accepts("image/png")).Returns(true);
        engine.Setup(mock => mock.CreateAttachmentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null))).Returns(new ProcessStartInfo("attached"));
        engine.Setup(mock => mock.Frame(It.IsAny<PromptInput>())).Returns((PromptInput turn) => $"FRAMED:{turn.Text}:{turn.Attachments.Count}");
    }

    [Fact]
    public async Task Send_WithAnImage_RunsTheAttachmentProcessWithTheFramedPrompt()
    {
        PromptSession session = Session(Record());
        Accepting();
        runner.Setup(mock => mock.Run(engine.Object, It.Is<ProcessStartInfo>(start => start.FileName == "attached"), "FRAMED:look:1", It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "seen")));

        Assert.Equal(["seen"], await session.Messages("look", new PromptRequestOptions { Attachments = [Image()] }));

        engine.Verify(mock => mock.CreateStart(It.IsAny<PromptEngineStart>()), Times.Never);
        engine.Verify(mock => mock.CreateAttachmentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond() && start.Files.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task Send_WithOnlyATextFile_PutsItIntoThePromptAndUsesTheOrdinaryProcess()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "read")));
        PromptAttachment text = new() { Name = "n.txt", MediaType = "text/plain", Content = Encoding.UTF8.GetBytes("hello") };

        await session.Messages("go", new PromptRequestOptions { Attachments = [text] });

        runner.Verify(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "go\n\nAttached file \"n.txt\":\n```\nhello\n```", It.IsAny<CancellationToken>()), Times.Once);
        engine.Verify(mock => mock.CreateAttachmentStart(It.IsAny<PromptEngineStart>()), Times.Never);
    }

    [Fact]
    public async Task Send_ForAProviderThatNeedsFiles_WritesThemForTheLengthOfThePromptAndRemovesThem()
    {
        using TempFolder temp = new();
        PromptSession session = Session(Record() with { WorkingFolder = temp.Path });
        Accepting();
        engine.SetupGet(mock => mock.NeedsFiles).Returns(true);
        List<string> seen = [];
        engine.Setup(mock => mock.CreateAttachmentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null))).Returns((PromptEngineStart given) =>
        {
            seen.AddRange(given.Files);
            return new ProcessStartInfo("attached");
        });
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "seen")));
        string? existedDuring = null;
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            existedDuring = string.Join(",", seen.Select(file => $"{Path.GetFileName(file)}={File.ReadAllBytes(file).Length}"));
            return Events(Event(PromptEngineEventKind.Reply, "seen"));
        });

        await session.Messages("look", new PromptRequestOptions { Attachments = [Image("one.png", 1, 2, 3), Image("two.png", 9)] });

        Assert.Equal("attachment-0.png=3,attachment-1.png=1", existedDuring);
        Assert.All(seen, file => Assert.StartsWith(temp.Path, file));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Send_RemovesTheFilesAlsoWhenThePromptFailsOrIsCancelled()
    {
        using TempFolder temp = new();
        PromptSession session = Session(Record() with { WorkingFolder = temp.Path });
        Accepting();
        engine.SetupGet(mock => mock.NeedsFiles).Returns(true);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Failed, "broke")));

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Messages("look", new PromptRequestOptions { Attachments = [Image()] }));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));

        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((IPromptEngine _, ProcessStartInfo _, string _, CancellationToken token) => UntilCancelled(token));
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Messages("look", new PromptRequestOptions { Attachments = [Image()] }, cancellation: cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task Send_WithAnAttachmentTheProviderCannotTake_ThrowsBeforeAnythingIsStarted()
    {
        PromptSession session = Session(Record());
        engine.SetupGet(mock => mock.Identifier).Returns("Codex");
        PromptAttachment zip = new() { Name = "a.zip", MediaType = "application/zip", Content = new byte[] { 1 } };

        await Assert.ThrowsAsync<NotSupportedException>(() => session.Messages("look", new PromptRequestOptions { Attachments = [zip] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.Messages("look", new PromptRequestOptions { Attachments = [Image()] }));

        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        toolHost.Verify(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>()), Times.Never);
    }

    [Fact]
    public async Task Send_WhenPersistentWithAnImage_GivesTheImageToTheLiveProcess()
    {
        PromptSession session = Persistent();
        Accepting();
        PromptAttachment image = Image();
        process.Setup(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "look" && turn.Attachments.Count == 1 && turn.Attachments[0] == image), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "seen"), Event(PromptEngineEventKind.Finished)));

        Assert.Equal(["seen"], await session.Messages("look", new PromptRequestOptions { Attachments = [image] }));

        process.Verify(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "look"), It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnableShell_IsOffByDefaultAndOffersNothing()
    {
        PromptSession session = Session(Record());
        Plain();

        await session.Messages("one");

        Assert.False(session.Configuration.EnableShell);
        Served();
        libraryTools.Verify(mock => mock.CreateShell(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EnableShell_OffersTheShellToolBesideTheOthersWithoutListingItInTools()
    {
        PromptSessionRecord record = Record();
        PromptSession session = Session(record);
        Library(Tool("read_file"));
        Mock<IPromptToolServer> server = Server();
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(server.Object);
        Plain();
        session.Configuration.EnableShell = true;
        session.Configuration.Mounts["main"] = new Mock<IStore>().Object;
        session.Configuration.Tools.Add(Tool("mine"));

        await session.Messages("one");
        await session.Messages("two");

        Served("read_file", "shell", "mine");
        libraryTools.Verify(mock => mock.CreateShell(record.WorkingFolder), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles() && start.HasShell())), Times.Exactly(2));
        Assert.Equal(["mine"], session.Configuration.Tools.Select(tool => tool.Name));
        Assert.True(session.Configuration.EnableShell);
    }

    [Fact]
    public async Task EnableImages_AndEnableWeb_AreOffByDefaultAndOfferNothing()
    {
        PromptSession session = Session(Record());
        engine.Setup(mock => mock.Accepts("image/png")).Returns(true);
        Library(Tool("read_file"));
        session.Configuration.Mounts["main"] = new Mock<IStore>().Object;
        Plain();

        await session.Messages("one");

        Assert.False(session.Configuration.EnableImages);
        Assert.False(session.Configuration.EnableWeb);
        Served("read_file");
        libraryTools.Verify(mock => mock.CreateImage(It.IsAny<IStore>(), It.IsAny<Func<string, PromptAttachment, CancellationToken, Task>>()), Times.Never);
        libraryTools.Verify(mock => mock.CreateFetch(It.IsAny<Func<Uri, CancellationToken, Task<bool>>>()), Times.Never);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task EnableImages_OffersTheImageToolOnlyWithFilesToReadAndAnEngineThatCanBeShownImages(bool mounted, bool engineTakesImages, bool offered)
    {
        PromptSession session = Session(Record());
        engine.Setup(mock => mock.Accepts("image/png")).Returns(engineTakesImages);
        libraryTools.Setup(mock => mock.CreateImage(It.IsAny<IStore>(), It.IsAny<Func<string, PromptAttachment, CancellationToken, Task>>())).Returns(() => Tool("view_image"));
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        session.Configuration.EnableImages = true;

        if (mounted)
        {
            session.Configuration.Mounts["main"] = new Mock<IStore>().Object;
        }

        await session.Messages("one");

        string[] expected = [.. mounted ? ["read_file"] : Array.Empty<string>(), .. offered ? ["view_image"] : Array.Empty<string>()];
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(expected.Append("respond"))), It.IsAny<IPromptToolHooks?>()), Times.Once);
    }

    [Fact]
    public async Task EnableImages_GivesTheImageToTheAiAsAnInputOfItsOwnWithTheImageAttachedAndANote()
    {
        PromptSession session = Session(Record());
        Func<string, PromptAttachment, CancellationToken, Task>? give = null;
        List<PromptInput> framed = [];
        engine.Setup(mock => mock.Accepts("image/png")).Returns(true);
        engine.Setup(mock => mock.CreateAttachmentStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("attached"));
        engine.Setup(mock => mock.Frame(It.IsAny<PromptInput>())).Returns((PromptInput turn) =>
        {
            framed.Add(turn);
            return turn.Text;
        });
        libraryTools.Setup(mock => mock.CreateImage(It.IsAny<IStore>(), It.IsAny<Func<string, PromptAttachment, CancellationToken, Task>>())).Callback<IStore, Func<string, PromptAttachment, CancellationToken, Task>>((_, given) => give = given).Returns(() => Tool("view_image"));
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Answers();
        session.Configuration.EnableImages = true;
        session.Configuration.Mounts["main"] = new Mock<IStore>().Object;
        await session.Messages("look");
        PromptAttachment image = new() { Name = "a.png", MediaType = "image/png", Content = new byte[] { 1, 2, 3 } };

        await give!("docs/a.png", image, CancellationToken.None);

        for (int attempt = 0; attempt < 500 && framed.Count < 1; attempt++)
        {
            await Task.Delay(10);
        }

        await Idle(session);
        PromptInput turn = Assert.Single(framed);
        Assert.Equal("Here is the image docs/a.png you asked to see.", turn.Text);
        Assert.Equal([image], turn.Attachments);
        Assert.Contains("view_image", Assert.Single(turn.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableImages_DuringARequest_SteersTheImageIntoTheWorkOfTheAiInsteadOfRefusingIt()
    {
        PromptSession session = Persistent();
        Func<string, PromptAttachment, CancellationToken, Task>? give = null;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<PromptInput> steered = [];
        engine.Setup(mock => mock.Accepts("image/png")).Returns(true);
        libraryTools.Setup(mock => mock.CreateImage(It.IsAny<IStore>(), It.IsAny<Func<string, PromptAttachment, CancellationToken, Task>>())).Callback<IStore, Func<string, PromptAttachment, CancellationToken, Task>>((_, given) => give = given).Returns(() => Tool("view_image"));
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => started.TrySetResult()));
        process.Setup(mock => mock.Steer(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Callback<PromptInput, CancellationToken>((input, _) => steered.Add(input)).ReturnsAsync(true);
        session.Configuration.EnableImages = true;
        session.Configuration.Mounts["main"] = new Mock<IStore>().Object;
        Task<List<string>> running = session.Messages("look");
        await started.Task;
        PromptAttachment image = new() { Name = "a.png", MediaType = "image/png", Content = new byte[] { 1, 2, 3 } };

        Assert.Equal(PromptSessionState.Responding, session.State);
        await give!("docs/a.png", image, CancellationToken.None);
        release.SetResult();
        await running;

        PromptInput input = Assert.Single(steered);
        Assert.Equal([image], input.Attachments);
        Assert.Contains("view_image", Assert.Single(input.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableImages_DuringARequestOfAnEngineThatCannotSteer_WaitsAndIsGivenAsWorkOnceTheRequestIsOver()
    {
        PromptSession session = Session(Record());
        Func<string, PromptAttachment, CancellationToken, Task>? give = null;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> prompts = [];
        engine.Setup(mock => mock.Accepts("image/png")).Returns(true);
        engine.Setup(mock => mock.CreateAttachmentStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("attached"));
        engine.Setup(mock => mock.Frame(It.IsAny<PromptInput>())).Returns((PromptInput turn) =>
        {
            prompts.Add(turn.Text);
            return turn.Text;
        });
        libraryTools.Setup(mock => mock.CreateImage(It.IsAny<IStore>(), It.IsAny<Func<string, PromptAttachment, CancellationToken, Task>>())).Callback<IStore, Func<string, PromptAttachment, CancellationToken, Task>>((_, given) => give = given).Returns(() => Tool("view_image"));
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => started.TrySetResult()));
        session.Configuration.EnableImages = true;
        session.Configuration.Mounts["main"] = new Mock<IStore>().Object;
        Task<List<string>> running = session.Messages("look");
        await started.Task;

        await give!("docs/a.png", new PromptAttachment { Name = "a.png", MediaType = "image/png", Content = new byte[] { 1 } }, CancellationToken.None);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events());
        release.SetResult();
        await running;

        await Idle(session);
        Assert.Contains(prompts, prompt => prompt.Contains("docs/a.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnableWeb_OffersTheFetchToolEvenWithoutFilesAndOnlyWhileItIsOn()
    {
        PromptSession session = Session(Record());
        libraryTools.Setup(mock => mock.CreateFetch(It.IsAny<Func<Uri, CancellationToken, Task<bool>>>())).Returns(() => Tool("fetch_url"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        session.Configuration.EnableWeb = true;

        await session.Messages("one");
        session.Configuration.EnableWeb = false;
        await session.Messages("two");

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(new[] { "fetch_url", "respond" })), It.IsAny<IPromptToolHooks?>()), Times.Once);
        libraryTools.Verify(mock => mock.CreateFetch(It.IsAny<Func<Uri, CancellationToken, Task<bool>>>()), Times.Once);
    }

    [Fact]
    public async Task WebFilter_IsReadWhenAnAddressIsRequestedSoItCanChangeAndAFailureOfItBlocks()
    {
        PromptSession session = Session(Record());
        Func<Uri, CancellationToken, Task<bool>>? isBlocked = null;
        libraryTools.Setup(mock => mock.CreateFetch(It.IsAny<Func<Uri, CancellationToken, Task<bool>>>())).Callback<Func<Uri, CancellationToken, Task<bool>>>(given => isBlocked = given).Returns(() => Tool("fetch_url"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        session.Configuration.EnableWeb = true;
        await session.Messages("one");
        Uri address = new("https://example.com/");

        Assert.False(await isBlocked!(address, CancellationToken.None));
        session.Configuration.WebFilter = (given, _) => Task.FromResult(given.Host == "example.com");
        Assert.True(await isBlocked(address, CancellationToken.None));
        session.Configuration.WebFilter = (_, _) => throw new InvalidOperationException("broken");
        Assert.True(await isBlocked(address, CancellationToken.None));
        session.Configuration.WebFilter = (_, cancellation) => Task.FromCanceled<bool>(new CancellationToken(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => isBlocked(address, CancellationToken.None));
        session.Configuration.WebFilter = null;
        Assert.False(await isBlocked(address, CancellationToken.None));
        libraryTools.Verify(mock => mock.CreateFetch(It.IsAny<Func<Uri, CancellationToken, Task<bool>>>()), Times.Once);
    }

    [Fact]
    public async Task TheTimeTheSearchOfToolsAndTheDelegationAreAlwaysOffered()
    {
        PromptSession session = Session(Record());
        Plain();

        await session.Messages("one");

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).SequenceEqual(new[] { "current_time", "tool_search", "delegate", "respond" })), It.IsAny<IPromptToolHooks?>()), Times.Once);
    }

    [Fact]
    public async Task EnableSearch_OffersTheLibrarysSearchToAnEngineThatHasNoneOfItsOwn()
    {
        PromptSession session = Session(Record());
        Plain();
        session.Configuration.EnableSearch = true;

        await session.Messages("one");

        Served("search_web");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => !start.Search)), Times.Once);
    }

    [Fact]
    public async Task EnableSearch_WithAnEngineThatSearchesItself_AllowsItsSearchAndOffersNoTool()
    {
        engine.SetupGet(mock => mock.NativeSearch).Returns(true);
        PromptSession session = Session(Record());
        Plain();
        session.Configuration.EnableSearch = true;

        await session.Messages("one");

        Served();
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Search)), Times.Once);
        libraryTools.Verify(mock => mock.CreateSearch(), Times.Never);
    }

    [Fact]
    public async Task EnableSearch_OffNeitherAllowsTheEnginesSearchNorOffersTheTool()
    {
        engine.SetupGet(mock => mock.NativeSearch).Returns(true);
        PromptSession session = Session(Record());
        Plain();

        await session.Messages("one");

        Assert.False(session.Configuration.EnableSearch);
        Served();
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => !start.Search)), Times.Once);
        libraryTools.Verify(mock => mock.CreateSearch(), Times.Never);
    }

    [Fact]
    public async Task EnableSearch_ChangedBetweenPrompts_RestartsAKeptProcessOfAnEngineWhoseSearchIsAllowedWhenItStarts()
    {
        engine.SetupGet(mock => mock.NativeSearch).Returns(true);
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.EnableSearch = true;
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
        process.Verify(mock => mock.Stop(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Delegate_RunsTheTaskInAFreshHelperOfTheSameModelAndWorkspaceAndReturnsItsAnswer()
    {
        Func<string, bool, CancellationToken, Task<string>>? run = null;
        libraryTools.Setup(mock => mock.CreateDelegate(It.IsAny<Func<string, bool, CancellationToken, Task<string>>>())).Callback<Func<string, bool, CancellationToken, Task<string>>>(given => run = given).Returns(() => Tool("delegate"));
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        PromptSession helper = Session(Record());
        PromptSessionOptions? created = null;
        provider.Setup(mock => mock.Create(It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).Callback<PromptSessionOptions?, CancellationToken>((options, _) => created = options).ReturnsAsync(helper);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "the helper found it"), Event(PromptEngineEventKind.Finished)));
        session.Configuration.Instructions = "Be brief.";
        await session.Messages("start");

        string answer = await run!("look into x", false, CancellationToken.None);

        Assert.Equal("the helper found it", answer);
        Assert.Equal(("sonnet", null), (created!.Model!.Id, created.WorkspacePath));
        Assert.Equal("Be brief.", helper.Configuration.Instructions);
        Assert.Contains("look into x", turns[^1].Text, StringComparison.Ordinal);
        Assert.Contains("delegated to you", turns[^1].Text, StringComparison.Ordinal);
        provider.Verify(mock => mock.Delete(helper.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(PromptSessionState.Waiting, helper.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => helper.Messages("again"));
    }

    [Fact]
    public async Task Delegate_GivesTheHelperNoDelegationOfItsOwn()
    {
        Func<string, bool, CancellationToken, Task<string>>? run = null;
        libraryTools.Setup(mock => mock.CreateDelegate(It.IsAny<Func<string, bool, CancellationToken, Task<string>>>())).Callback<Func<string, bool, CancellationToken, Task<string>>>(given => run = given).Returns(() => Tool("delegate"));
        Plain();
        PromptSession session = Session(Record());
        PromptSession helper = Session(Record());
        provider.Setup(mock => mock.Create(It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(helper);
        await session.Messages("start");

        await run!("task", false, CancellationToken.None);

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Any(tool => tool.Name == "delegate")), It.IsAny<IPromptToolHooks?>()), Times.Once);
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => !started.Any(tool => tool.Name == "delegate")), It.IsAny<IPromptToolHooks?>()), Times.Once);
    }

    [Fact]
    public async Task Delegate_WithTheContextShared_RunsTheTaskInAForkOfTheConversationThatIsThrownAway()
    {
        Func<string, bool, CancellationToken, Task<string>>? run = null;
        libraryTools.Setup(mock => mock.CreateDelegate(It.IsAny<Func<string, bool, CancellationToken, Task<string>>>())).Callback<Func<string, bool, CancellationToken, Task<string>>>(given => run = given).Returns(() => Tool("delegate"));
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        PromptSession fork = Session(Record());
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>())).Returns(fork);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "forked answer"), Event(PromptEngineEventKind.Finished)));
        await session.Messages("start");

        string answer = await run!("review the plan", true, CancellationToken.None);

        Assert.Equal("forked answer", answer);
        Assert.Contains("review the plan", turns[^1].Text, StringComparison.Ordinal);
        Assert.Contains("The request the other AI is working on now:\nstart", turns[^1].Text, StringComparison.Ordinal);
        Assert.Contains("continue its conversation", turns[^1].Text, StringComparison.Ordinal);
        provider.Verify(mock => mock.Delete(fork.Id, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(mock => mock.Create(It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delegate_WhenTheHelperFails_FailsTheCallWithItsReasonAndStillRemovesTheHelper()
    {
        Func<string, bool, CancellationToken, Task<string>>? run = null;
        libraryTools.Setup(mock => mock.CreateDelegate(It.IsAny<Func<string, bool, CancellationToken, Task<string>>>())).Callback<Func<string, bool, CancellationToken, Task<string>>>(given => run = given).Returns(() => Tool("delegate"));
        Plain();
        PromptSession session = Session(Record());
        PromptSession helper = Session(Record());
        provider.Setup(mock => mock.Create(It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(helper);
        await session.Messages("start");
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Failed, "the helper broke")));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => run!("task", false, CancellationToken.None));

        Assert.Equal("the helper broke", exception.Message);
        provider.Verify(mock => mock.Delete(helper.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnableShell_WithoutMounts_ServesOnlyTheShellAndSaysSo()
    {
        PromptSession session = Session(Record());
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        session.Configuration.EnableShell = true;

        await session.Messages("one");

        Served("shell");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && !start.HasFiles() && start.HasShell())), Times.Once);
    }

    [Fact]
    public async Task Workspace_GivesTheFileToolsAWorkspaceAndTellsTheAiSo()
    {
        PromptSession session = Session(Record());
        IStore workspace = new Mock<IStore>().Object;
        IStore? built = null;
        bool? hasWorkspace = null;
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns((IStore store, IReadOnlyList<string> _, bool given) =>
        {
            built = store;
            hasWorkspace = given;
            return [Tool("read_file")];
        });
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        session.Configuration.Workspace = workspace;

        await session.Messages("one");

        Assert.IsType<MountedStore>(built);
        Assert.True(hasWorkspace);
        Served("read_file");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles() && start.HasWorkspace() && !start.ShellInWorkspace())), Times.Once);
    }

    [Fact]
    public async Task Workspace_ChangedBetweenPrompts_RebuildsTheFileToolsAndReplacesTheServer()
    {
        PromptSession session = Session(Record());
        Library(Tool("read_file"));
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server("http://127.0.0.1:1/mcp").Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        Plain();
        session.Configuration.Workspace = new Mock<IStore>().Object;

        await session.Messages("one");
        await session.Messages("two");
        session.Configuration.Workspace = new Mock<IStore>().Object;
        await session.Messages("three");

        libraryTools.Verify(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), true), Times.Exactly(2));
    }

    [Fact]
    public async Task Workspace_OfAFolderOnDisk_IsWhereTheShellStartsAndTheAiIsToldSo()
    {
        PromptSession session = Session(Record());
        Mock<IDiskStore> disk = new();
        disk.SetupGet(mock => mock.Path).Returns("/projects/demo");
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        session.Configuration.Workspace = disk.Object;
        session.Configuration.EnableShell = true;

        await session.Messages("one");

        libraryTools.Verify(mock => mock.CreateShell("/projects/demo"), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasShell() && start.ShellInWorkspace())), Times.Once);
    }

    [Fact]
    public async Task Workspace_ChangedToAnotherFolder_StartsTheShellThere()
    {
        PromptSession session = Session(Record());
        Mock<IDiskStore> first = new();
        first.SetupGet(mock => mock.Path).Returns("/one");
        Mock<IDiskStore> second = new();
        second.SetupGet(mock => mock.Path).Returns("/two");
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(() => Server().Object);
        Plain();
        session.Configuration.EnableShell = true;
        session.Configuration.Workspace = first.Object;
        await session.Messages("one");

        session.Configuration.Workspace = second.Object;
        await session.Messages("two");
        session.Configuration.Workspace = null;
        await session.Messages("three");

        libraryTools.Verify(mock => mock.CreateShell("/one"), Times.Once);
        libraryTools.Verify(mock => mock.CreateShell("/two"), Times.Once);
        libraryTools.Verify(mock => mock.CreateShell(Record().WorkingFolder), Times.Once);
    }

    [Fact]
    public async Task EnableShell_TurnedOffAgain_ReplacesTheServerWithoutTheShell()
    {
        PromptSession session = Session(Record());
        Mock<IPromptToolServer> first = Server("http://127.0.0.1:1/mcp");
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(first.Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        Plain();
        session.Configuration.EnableShell = true;
        session.Configuration.Tools.Add(Tool("mine"));
        await session.Messages("one");

        session.Configuration.EnableShell = false;
        await session.Messages("two");

        first.Verify(mock => mock.DisposeAsync(), Times.Once);
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(new[] { "mine", "respond" })), It.IsAny<IPromptToolHooks?>()), Times.Once);
    }

    [Fact]
    public async Task EnableShell_WhenPersistentAndChanged_StartsANewProcess()
    {
        PromptSession session = Persistent();
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server("http://127.0.0.1:1/mcp").Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        session.Configuration.EnableShell = true;
        await session.Messages("one");

        session.Configuration.EnableShell = false;
        session.Configuration.Tools.Add(Tool("mine"));
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
    }

    [Fact]
    public void Tools_RejectTheNameOfTheShellTool()
    {
        PromptSession session = Session(Record());

        Assert.Throws<ArgumentException>(() => session.Configuration.Tools.Add(Tool("shell")));
    }

    [Fact]
    public async Task Send_WithMounts_BuildsTheLibraryToolsOverTheMountedView()
    {
        PromptSession session = LibrarySession(mount: false);
        Mock<IStore> docs = new();
        IStore? built = null;
        IReadOnlyList<string>? names = null;
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns((IStore system, IReadOnlyList<string> mounts, bool _) =>
        {
            built = system;
            names = mounts;
            return [Tool("read_file")];
        });
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        session.Configuration.Mounts["docs"] = docs.Object;

        await session.Messages("one");

        Assert.IsType<MountedStore>(built);
        Assert.Equal(["docs"], names);
    }

    [Fact]
    public async Task Send_WithoutMounts_OffersNoFileToolsAndServesOnlyTheRespondTool()
    {
        PromptSession session = LibrarySession(mount: false);
        Library(Tool("read_file"));
        Plain();

        await session.Messages("one");

        libraryTools.Verify(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()), Times.Never);
        Served();
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
    }

    [Fact]
    public async Task Send_WithoutMountsButWithCallerTools_ServesOnlyThoseAndSaysTheAiHasNoFiles()
    {
        PromptSession session = LibrarySession(mount: false);
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        SetTools(session, Tool("mine"));

        await session.Messages("one");

        Served("mine");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && !start.HasFiles())), Times.Once);
    }

    [Fact]
    public async Task Send_WithMounts_SaysTheAiHasFiles()
    {
        PromptSession session = LibrarySession();
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();

        await session.Messages("one");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles())), Times.Once);
    }

    [Fact]
    public async Task Send_WhenAllMountsAreRemoved_ReplacesTheServerWithOneThatOffersOnlyTheRespondTool()
    {
        PromptSession session = LibrarySession();
        Mock<IPromptToolServer> server = Server();
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(server.Object);
        Plain();
        await session.Messages("one");

        session.Configuration.Mounts.Clear();
        await session.Messages("two");

        server.Verify(mock => mock.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Send_WhenTheMountsChange_RebuildsTheToolsAndReplacesTheServer()
    {
        PromptSession session = LibrarySession();
        Mock<IPromptToolServer> first = Server("http://127.0.0.1:1/mcp");
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(first.Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns(() => [Tool("read_file")]);
        Plain();
        await session.Messages("one");

        session.Configuration.Mounts["docs"] = new Mock<IStore>().Object;
        await session.Messages("two");

        first.Verify(mock => mock.DisposeAsync(), Times.Once);
        libraryTools.Verify(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Send_WithLibraryTools_OffersThemWithTheCallersToolsAndBuildsThemOnce()
    {
        PromptSession session = LibrarySession();
        Library(Tool("read_file"), Tool("write_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        Plain();
        SetTools(session, Tool("mine"));

        await session.Messages("one");
        await session.Messages("two");

        Served("read_file", "write_file", "mine");
        libraryTools.Verify(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Send_WithLibraryToolsThatAreNotAnyAndNoCallerTools_ServesOnlyTheRespondTool()
    {
        PromptSession session = LibrarySession();
        Library();
        Plain();

        await session.Messages("one");

        Served();
    }

    [Fact]
    public void Tools_RejectTheNameOfALibraryFileTool()
    {
        PromptSession session = LibrarySession();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => SetTools(session, Tool("read_file")));

        Assert.Contains("library tool", exception.Message);
        SetTools(session, Tool("read_files"));
    }

    [Fact]
    public async Task Ask_JoinsEveryReply()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), "hi", It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Reply, "one"), Event(PromptEngineEventKind.Reply, "two")));

        Assert.Equal("one\n\ntwo", await session.Request("hi"));
    }

    private async IAsyncEnumerable<PromptEngineEvent> UntilCancelled([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await Task.Delay(Timeout.Infinite, cancellation);
        yield return Event(PromptEngineEventKind.Reply, "never");
    }

    private async IAsyncEnumerable<PromptEngineEvent> Blocked(Task release, Action started)
    {
        started();
        await release;
        yield return Event(PromptEngineEventKind.Reply, "done");
    }

    [Fact]
    public async Task Settings_AfterDisposal_RefuseChanges()
    {
        PromptSession session = Session(Record());
        await session.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => session.Configuration.ModelSettings["effort"] = "high");
        Assert.Throws<ObjectDisposedException>(() => session.Configuration.Tools.Add(Tool("late")));
        Assert.Throws<ObjectDisposedException>(() => session.Configuration.Mounts["late"] = new Mock<IStore>().Object);
        Assert.Equal("medium", session.Configuration.ModelSettings["effort"]);
    }

    [Fact]
    public void Settings_ThatAreNotAllowedThrowAtOnceAndChangeNothing()
    {
        PromptSession session = Session(Record());
        session.Configuration.Mounts["docs"] = new Mock<IStore>().Object;

        Assert.Throws<ArgumentException>(() => session.Configuration.Mounts["bad//name"] = new Mock<IStore>().Object);
        Assert.Throws<ArgumentException>(() => session.Configuration.Tools.Add(Tool("bad name")));
        Assert.Throws<ArgumentException>(() => session.Configuration.ModelSettings["effort"] = "extreme");

        Assert.Equal(["docs"], session.Configuration.Mounts.Keys);
        Assert.Equal("medium", session.Configuration.ModelSettings["effort"]);
        Assert.Empty(session.Configuration.Tools);
    }

    [Fact]
    public async Task SeveralChangesBeforeAPrompt_CostOneRestartOfAKeptProcess()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.ModelSettings["effort"] = "high";
        session.Configuration.Model = new PromptModel { Id = "opus", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"])] };
        session.Configuration.Instructions = "Be brief.";
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
        engine.Verify(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.Model == "opus" && start.Settings["effort"] == "high" && start.Instructions.EndsWith("Be brief.") && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
    }

    [Fact]
    public async Task AChangeWhileAPromptRuns_DoesNotReachThatPromptAndSettingsAreTakenTogetherAtTheNext()
    {
        PromptSession session = Session(Record());
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => { }));
        Task<List<string>> first = session.Messages("one");
        await Task.Delay(100);

        session.Configuration.Model = new PromptModel { Id = "opus", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high", "max"])] };
        session.Configuration.Tools.Add(Tool("late"));
        release.SetResult();
        await first;
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events());
        await session.Messages("two");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Model == "sonnet" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Model == "opus" && start.ApiKey == null && start.Schema == null)), Times.Once);
    }

    [Fact]
    public async Task Send_RunsWithChangedSettingsAndStoresTheModelAndItsSettingsOnce()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events());
        session.Configuration.Model = new PromptModel { Id = "opus", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium"])] };
        session.Configuration.ModelSettings["effort"] = "low";

        await session.Messages("one");
        await session.Messages("two");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Model == "opus" && start.Settings["effort"] == "low" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Exactly(2));
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Model == "opus" && link.ModelSettings!["effort"] == "low"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ANewSession_StartsWithTheDefaultSettings()
    {
        IPromptSessionConfiguration configuration = Session(Record()).Configuration;

        Assert.Equal(("sonnet", "medium", false, false), (configuration.Model!.Id, configuration.ModelSettings["effort"], configuration.IsPersistent, configuration.EnableShell));
        Assert.Empty(configuration.Mounts);
        Assert.Empty(configuration.Tools);
        Assert.Empty(configuration.McpServers);
        Assert.Null(configuration.Instructions);
        Assert.Null(configuration.BeforeToolCall);
        Assert.Null(configuration.Workspace);
        Assert.Null(configuration.Limits.Budget);
        Assert.Null(configuration.Limits.Retry);
        Assert.Null(configuration.Limits.Timeout);
    }

    [Fact]
    public async Task NoSettingIsStoredWhateverTheSessionDoesOrWhenItIsDisposed()
    {
        PromptSession graceful = Session(Record());
        PromptSession immediate = Session(Record());
        Plain();
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        graceful.Configuration.EnableShell = true;
        graceful.Configuration.IsPersistent = true;
        graceful.Configuration.Instructions = "Be brief.";
        graceful.Configuration.Mounts["app"] = IStore.FromDisk(AppContext.BaseDirectory);
        graceful.Configuration.McpServers["db"] = PromptMcpServer.Http("https://example.com");
        graceful.Configuration.Tools.Add(Tool("mine"));
        immediate.Configuration.EnableShell = true;

        await graceful.Messages("one");
        immediate.Dispose();
        await immediate.DisposeAsync();
        await graceful.DisposeAsync();

        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private List<PromptInput> Turns()
    {
        List<PromptInput> turns = [];
        engine.Setup(mock => mock.Plain(It.IsAny<PromptInput>())).Returns((PromptInput turn) =>
        {
            turns.Add(turn);
            return turn.Text;
        });
        return turns;
    }

    private void Answers(string? aiSessionId = null)
        => runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Events([.. aiSessionId is null ? Array.Empty<PromptEngineEvent>() : [Event(PromptEngineEventKind.Session, aiSessionId)], Event(PromptEngineEventKind.Reply, "ok"), Event(PromptEngineEventKind.Finished)]));

    [Fact]
    public async Task Instructions_OnTheFirstPromptOfANewSession_AreNotToldAsANoteSinceTheyStartTheConversation()
    {
        engine.SetupGet(mock => mock.KeepsInstructions).Returns(true);
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        Answers();
        session.Configuration.Instructions = "Be brief.";

        await session.Request("go");

        Assert.Empty(turns.Single().Notes);
    }

    [Fact]
    public async Task Instructions_OnTheFirstPromptOfALoadedSession_AreToldAsANoteToAToolThatKeepsTheOnesItBeganWith()
    {
        engine.SetupGet(mock => mock.KeepsInstructions).Returns(true);
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record("abc"));
        Answers();
        session.Configuration.Instructions = "Be brief.";

        await session.Request("go");
        await session.Request("again");

        Assert.Contains("Be brief.", Assert.Single(turns[0].Notes));
        Assert.Empty(turns[1].Notes);
    }

    [Fact]
    public async Task AttachTo_ToAToolThatKeepsInstructions_TellsTheCurrentOnesAsANoteBecauseTheNewConversationsAreUnknown()
    {
        engine.SetupGet(mock => mock.KeepsInstructions).Returns(true);
        engine.Setup(mock => mock.HasSession("other")).Returns(true);
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record("abc"));
        Answers();
        session.Configuration.Instructions = "Be brief.";
        await session.Request("go");

        await session.AttachTo("other");
        await session.Request("again");

        Assert.Contains("Be brief.", Assert.Single(turns[0].Notes));
        Assert.Contains("Be brief.", Assert.Single(turns[1].Notes));
    }

    [Fact]
    public async Task Instructions_OfAToolThatTakesThemWithEveryLaunch_AreNeverToldAsANote()
    {
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record("abc"));
        Answers();
        session.Configuration.Instructions = "Be brief.";

        await session.Request("go");
        session.Configuration.Instructions = "Be long.";
        await session.Request("again");

        Assert.All(turns, turn => Assert.Empty(turn.Notes));
    }

    [Fact]
    public async Task NoInstructions_OnTheFirstPromptOfALoadedSession_AreNotToldAtAll()
    {
        engine.SetupGet(mock => mock.KeepsInstructions).Returns(true);
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record("abc"));
        Answers();

        await session.Request("go");

        Assert.Empty(turns.Single().Notes);
    }

    [Fact]
    public async Task Instructions_ChangedBetweenPrompts_AreToldOnceWhateverTheyAreAndWhenTheyAreCleared()
    {
        engine.SetupGet(mock => mock.KeepsInstructions).Returns(true);
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        Answers("abc");
        session.Configuration.Instructions = "Be brief.";
        await session.Request("one");

        session.Configuration.Instructions = "Answer in French.";
        await session.Request("two");
        await session.Request("three");
        session.Configuration.Instructions = null;
        await session.Request("four");

        Assert.Empty(turns[0].Notes);
        Assert.Contains("Answer in French.", Assert.Single(turns[1].Notes));
        Assert.Empty(turns[2].Notes);
        Assert.Contains("no longer apply", Assert.Single(turns[3].Notes));
    }

    [Fact]
    public async Task Instructions_OfAPromptThatFailedBeforeAnythingHappened_AreToldAgainWithTheNext()
    {
        engine.SetupGet(mock => mock.KeepsInstructions).Returns(true);
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record("abc"));
        runner.SetupSequence(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Failed, "not now")))
            .Returns(Events(Event(PromptEngineEventKind.Reply, "ok"), Event(PromptEngineEventKind.Finished)));
        session.Configuration.Instructions = "Be brief.";

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));
        await session.Request("go");

        Assert.Equal("not now", exception.Message);
        Assert.All(turns, turn => Assert.Contains("Be brief.", Assert.Single(turn.Notes)));
        Assert.Equal(2, turns.Count);
    }

    [Fact]
    public async Task Fork_ContinuesTheConversationWithTheInstructionsItHoldsAndOnlyTellsWhatDiffers()
    {
        engine.SetupGet(mock => mock.KeepsInstructions).Returns(true);
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record("abc"));
        PromptSession branch = Session(Record("copy"));
        Answers();
        engine.Setup(mock => mock.Branch(It.Is<PromptEngineStart>(start => start.AiSessionId == "abc"), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { AiSessionId = "copy" });
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>())).Returns(branch);
        session.Configuration.Instructions = "Be brief.";
        await session.Request("go");

        IPromptSession fork = await session.Fork();
        await fork.Request("go");
        fork.Configuration.Instructions = "Answer in French.";
        await fork.Request("again");

        Assert.Equal(3, turns.Count);
        Assert.Contains("Be brief.", Assert.Single(turns[0].Notes));
        Assert.Empty(turns[1].Notes);
        Assert.Contains("Answer in French.", Assert.Single(turns[2].Notes));
    }

    [Fact]
    public async Task Send_WithANote_DeliversTheNoteWithThatInputOnly()
    {
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        Answers();

        await session.Send("go", new PromptOptions { Notes = ["first"] });
        await Idle(session);
        await session.Send("again");
        await Idle(session);

        Assert.Equal(["first"], turns[0].Notes);
        Assert.Empty(turns[1].Notes);
    }

    [Fact]
    public async Task Send_WithSeveralNotes_DeliversThemInOrder()
    {
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        Answers();

        await session.Send("go", new PromptOptions { Notes = ["one", "two", "three"] });
        await Idle(session);

        Assert.Equal(["one", "two", "three"], Assert.Single(turns).Notes);
    }

    [Fact]
    public async Task Send_ANoteThatWasNotDeliveredBecauseTheWorkFailed_StaysForTheNextInput()
    {
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        runner.SetupSequence(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Failed, "not now")))
            .Returns(Events(Event(PromptEngineEventKind.Reply, "ok"), Event(PromptEngineEventKind.Finished)));

        await session.Send("go", new PromptOptions { Notes = ["keep me"] });
        await Idle(session);
        await session.Send("go", new PromptOptions { Notes = ["and me"] });
        await Idle(session);

        Assert.Equal(["keep me"], turns[0].Notes);
        Assert.Equal(["keep me", "and me"], turns[1].Notes);
    }

    [Fact]
    public async Task Request_WithANote_DeliversTheNoteWithThatPromptOnly()
    {
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        Answers();

        await session.Request("go", new PromptRequestOptions { Notes = ["the build passes"] });
        await session.Request("again");

        Assert.Equal(["the build passes"], turns[0].Notes);
        Assert.Empty(turns[1].Notes);
    }

    [Fact]
    public async Task Request_ABlankNote_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => Session(Record()).Request("go", new PromptRequestOptions { Notes = [" "] }));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Send_ABlankNote_Throws(string note)
        => await Assert.ThrowsAsync<ArgumentException>(() => Session(Record()).Send("go", new PromptOptions { Notes = [note] }));

    [Fact]
    public async Task Send_AfterDisposal_Throws()
    {
        PromptSession session = Session(Record());
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Send("late", new PromptOptions { Notes = ["late"] }));
    }
}
