namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<IPromptToolService> provider = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptProcess> process = new();

    private readonly Mock<IPromptToolHost> toolHost = new();

    private readonly Mock<IPromptLibraryTools> libraryTools = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private PromptSessionRecord Record(string? aiSessionId = null)
        => new PromptSessionRecordBuilder { AiSessionId = aiSessionId }.Build(engine.Object);

    public PromptSessionTests()
    {
        store.Setup(mock => mock.GetWorkspaceFolder(It.IsAny<Guid>())).Returns((Guid id) => Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id:N}"));
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        libraryTools.SetupGet(mock => mock.Names).Returns(["read_file"]);
        engine.Setup(mock => mock.Plain(It.IsAny<PromptInput>())).Returns((PromptInput turn) => turn.Text);
        libraryTools.SetupGet(mock => mock.ShellName).Returns("shell");
        libraryTools.OfferTheEverydayTools();
        libraryTools.Setup(mock => mock.CreateShell(It.IsAny<string>())).Returns(() => Tool("shell"));
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>())).Returns([]);
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(() => Server().Object);
        provider.Setup(mock => mock.Select(It.IsAny<PromptCapability>(), It.IsAny<PromptEffort>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptCapability capability, PromptEffort effort, string? _, CancellationToken _) => (
                new PromptModel { Id = capability switch { PromptCapability.Fast => "haiku", PromptCapability.Balanced => "sonnet", PromptCapability.Strong => "opus", _ => "fable" }, Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high", "max"])] },
                new Dictionary<string, string> { ["effort"] = effort.ToString().ToLowerInvariant() }));
    }

    private PromptSession Session(PromptSessionRecord record, string? apiKey = null, Func<PromptImageSetup, IPromptImageGenerator>? generators = null, Func<PromptImageSetup?>? images = null)
    {
        engine.Setup(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == apiKey))).Returns(new ProcessStartInfo("tool"));
        return new PromptSession(record, apiKey, store.Object, provider.Object, engine.Object, runner.Object, toolHost.Object, libraryTools.Object, opener.Object, generators: generators, images: images);
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
        Assert.Equal((PromptCapability.Balanced, PromptEffort.High), (session.Configuration.Capability, session.Configuration.Effort));
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
    public void Descriptors_StartWithTheProviderAndTheModelOfTheRecord()
    {
        PromptSession session = Session(Record());

        Assert.Equal(new Dictionary<string, string> { ["Provider"] = "Fake", ["Model"] = "sonnet", ["Effort"] = "High" }, session.Descriptors);
    }

    [Fact]
    public async Task Descriptors_ChangeWhenAPromptChoosesAnotherModelAndAnEventSaysSo()
    {
        PromptSession session = Session(Record());
        Plain();
        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);

        await session.Messages("one");
        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Effort = PromptEffort.Low;
        await session.Messages("two");
        await session.Messages("three");
        await recorder.Until(values => values.OfType<PromptDescriptorsEvent>().Any());

        Dictionary<string, string> expected = new() { ["Provider"] = "Fake", ["Model"] = "opus", ["Effort"] = "Low" };
        Assert.Equal(expected, session.Descriptors);
        PromptDescriptorsEvent changed = Assert.Single(recorder.Values.OfType<PromptDescriptorsEvent>());
        Assert.Equal(expected, changed.Descriptors);
    }

    [Fact]
    public async Task Send_ThrowsWhenTheAiFails()
    {
        PromptSession session = Session(Record());
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Events(Event(PromptEngineEventKind.Reply, "partial"), Event(PromptEngineEventKind.Failed, "broken")));

        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);
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

        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Effort = PromptEffort.Low;

        Assert.Equal((PromptCapability.Strong, PromptEffort.Low), (session.Configuration.Capability, session.Configuration.Effort));
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Settings_RejectValuesThatAreNotDefined()
    {
        PromptSession session = Session(Record());

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Configuration.Capability = (PromptCapability)99);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Configuration.Effort = (PromptEffort)99);
        Assert.Equal((PromptCapability.Balanced, PromptEffort.High), (session.Configuration.Capability, session.Configuration.Effort));
    }

    [Fact]
    public async Task Settings_ChangedWhileAPromptRuns_ApplyFromTheNextPrompt()
    {
        PromptSession session = Session(Record());
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => { }));
        Task<List<string>> first = session.Messages("one");
        await Task.Delay(100);

        session.Configuration.Effort = PromptEffort.Max;
        release.SetResult();
        await first;
        await session.Messages("two");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Settings["effort"] == "high" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
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
    public async Task Send_WhenTheKeptProcessDiedBetweenPrompts_StartsANewOneAndRunsThePromptThereOnce()
    {
        PromptSession session = Persistent();
        Mock<IPromptProcess> second = new();
        second.SetupGet(mock => mock.IsRunning).Returns(true);
        second.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "again"), Event(PromptEngineEventKind.Finished)));
        runner.Setup(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>())).Returns(new Queue<IPromptProcess>([process.Object, second.Object]).Dequeue);
        bool dead = false;
        process.SetupGet(mock => mock.IsRunning).Returns(() => !dead);
        int runs = 0;
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                return Events(Event(PromptEngineEventKind.Reply, "kept"), Event(PromptEngineEventKind.Finished));
            }

            dead = true;
            return Events(Event(PromptEngineEventKind.Failed, "Fake stopped."));
        });

        await session.Messages("one");
        List<string> replies = await session.Messages("two");

        Assert.Equal(["again"], replies);
        process.Verify(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        second.Verify(mock => mock.Run(It.Is<PromptInput>(input => input.Text == "two"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_WhenTheKeptProcessFailsAPromptAndStaysAlive_ReportsTheFailureWithoutTryingAgain()
    {
        PromptSession session = Persistent();
        int runs = 0;
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() => Interlocked.Increment(ref runs) == 1
            ? Events(Event(PromptEngineEventKind.Reply, "kept"), Event(PromptEngineEventKind.Finished))
            : Events(Event(PromptEngineEventKind.Failed, "Invalid API key")));
        await session.Messages("one");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Messages("two"));

        Assert.Equal("Invalid API key", exception.Message);
        process.Verify(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
                session.Transient.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");
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

        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Effort = PromptEffort.Max;
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Once);
        process.Verify(mock => mock.Run(It.Is<PromptInput>(turn => turn.Text == "one" && turn.Model == "sonnet" && turn.Settings["effort"] == "high"), It.IsAny<CancellationToken>()), Times.Once);
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
        session.Transient.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");
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

        session.Configuration.Capability = session.Configuration.Capability;
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
        session.Transient.Tools.Clear();

        foreach (PromptTool tool in tools)
        {
            session.Transient.Tools.Add(tool);
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

        Assert.Empty(session.Transient.Tools);
        SetTools(session, tool);
        Assert.Equal([tool], session.Transient.Tools);
    }

    [Theory]
    [InlineData("generate_image")]
    [InlineData("Generate_Image")]
    public void Tools_RejectTheNameOfTheImageTool(string name)
        => Assert.Throws<ArgumentException>(() => Session(Record()).Transient.Tools.Add(Tool(name)));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dot.ted")]
    [InlineData("ünï")]
    public void Tools_RejectAnInvalidName(string name)
        => Assert.Throws<ArgumentException>(() => Session(Record()).Transient.Tools.Add(Tool(name)));

    [Fact]
    public void Tools_RejectATooLongName()
        => Assert.Throws<ArgumentException>(() => Session(Record()).Transient.Tools.Add(Tool(new string('a', 65))));

    [Fact]
    public void Tools_RejectDuplicateNames()
    {
        PromptSession session = Session(Record());
        SetTools(session, Tool("alpha"));

        Assert.Throws<ArgumentException>(() => SetTools(session, Tool("beta"), Tool("beta")));
        Assert.Single(session.Transient.Tools);
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
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(server.Object);
        Plain();
        SetTools(session, Tool("alpha"), Tool("beta"));

        await session.Messages("one");
        await session.Messages("two");

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(new[] { "alpha", "beta", "respond" }))), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.Reaches(server.Object.Endpoint))), Times.Exactly(2));
        server.Verify(mock => mock.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task Send_WhenTheToolsChange_ReplacesTheServer()
    {
        PromptSession session = Session(new PromptSessionRecordBuilder().Build(engine.Object));
        Mock<IPromptToolServer> first = Server("http://127.0.0.1:1/mcp");
        Mock<IPromptToolServer> second = Server("http://127.0.0.1:2/mcp");
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(first.Object).Returns(second.Object);
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
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(server.Object);
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
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(server.Object);
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
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server("http://127.0.0.1:1/mcp").Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        SetTools(session, Tool("alpha"));

        await session.Messages("one");
        await session.Messages("two");
        SetTools(session, Tool("alpha"), Tool("beta"));
        await session.Messages("three");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
        engine.Verify(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.Servers[0].Url!.EndsWith(":2/mcp"))), Times.Once);
    }

    private PromptSession LibrarySession()
        => Session(new PromptSessionRecordBuilder().Build(engine.Object));

    private void Library(params PromptTool[] built)
        => libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>())).Returns(built);

    private readonly string[] everyday = ["current_time", "tool_search", "delegate"];

    private void Served(params string[] names)
        => toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(names.Append("respond")))), Times.Once);

    [Fact]
    public void Tools_AreAddedToAndRemovedFromOneAtATimeAcrossConfigureCalls()
    {
        PromptSession session = Session(Record());
        PromptTool alpha = Tool("alpha");

        session.Transient.Tools.Add(alpha);
        session.Transient.Tools.Add(Tool("beta"));
        session.Transient.Tools.Remove(alpha);

        Assert.Equal(["beta"], session.Transient.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task Cache_IsAnEmptyStoreOfTheSessionAndIsLostWhenItIsDisposed()
    {
        PromptSession session = Session(Record());
        Assert.Empty(await session.Cache.List(string.Empty).ToListAsync());

        await session.Cache.WriteText("notes/a.txt", "x");
        Assert.Equal("x", await session.Cache.ReadText("notes/a.txt"));
        await session.DisposeAsync();

        Assert.Empty(await session.Cache.List(string.Empty, true).ToListAsync());
    }

    [Fact]
    public void WorkspacePath_IsTheWorkingFolderOfTheRecordWhetherOrNotTheCallerGaveIt()
    {
        using TempFolder folder = new();

        Assert.Equal("/work", Session(Record()).WorkspacePath);
        Assert.Equal(folder.Path, Session(Record() with { WorkspacePath = folder.Path, WorkingFolder = folder.Path }).WorkspacePath);
    }

    [Fact]
    public async Task Send_BuildsTheFileToolsOverTheWorkspaceAndTheCacheAtTheCachePath()
    {
        PromptSession session = Session(Record());
        IStore? built = null;
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>())).Returns((IStore store) =>
        {
            built = store;
            return [Tool("read_file")];
        });
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();
        await session.Cache.WriteText("a.txt", "cached");

        await session.Messages("one");

        Assert.IsType<MountedStore>(built);
        Assert.Equal("cached", await built!.ReadText("CACHE:/a.txt"));
        Served("read_file");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles() && start.HasWorkspace() && start.Instructions.Contains("CACHE:"))), Times.Once);
    }

    [Fact]
    public async Task Send_WithAWorkspacePath_GivesTheFileToolsTheWorkspaceAndTellsTheAiSo()
    {
        using TempFolder folder = new();
        await File.WriteAllTextAsync(folder.Combine("a.txt"), "on disk");
        PromptSession session = Session(Record() with { WorkspacePath = folder.Path, WorkingFolder = folder.Path });
        IStore? built = null;
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>())).Returns((IStore store) =>
        {
            built = store;
            return [Tool("read_file")];
        });
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();

        await session.Messages("one");

        Assert.Equal("on disk", await built!.ReadText("a.txt"));
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles() && start.HasWorkspace())), Times.Once);
    }

    [Fact]
    public async Task Move_ANewWorkspace_RebuildsTheFileToolsAndReplacesTheServer()
    {
        using TempFolder first = new();
        using TempFolder second = new();
        PromptSession session = Session(Record() with { WorkspacePath = first.Path, WorkingFolder = first.Path });
        Library(Tool("read_file"));
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server("http://127.0.0.1:1/mcp").Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        Plain();

        await session.Messages("one");
        await session.Messages("two");
        await session.Move(second.Path);
        await session.Messages("three");

        libraryTools.Verify(mock => mock.Create(It.IsAny<IStore>()), Times.Exactly(2));
        Assert.Equal(second.Path, session.WorkspacePath);
    }

    [Fact]
    public async Task WorkspacePath_IsWhereTheShellStartsAndTheAiIsToldSo()
    {
        using TempFolder folder = new();
        PromptSession session = Session(Record() with { WorkspacePath = folder.Path, WorkingFolder = folder.Path });
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();
        session.Configuration.IsShellAllowed = true;

        await session.Messages("one");

        libraryTools.Verify(mock => mock.CreateShell(folder.Path), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasShell() && start.ShellInWorkspace())), Times.Once);
    }

    [Fact]
    public async Task Move_AnotherFolder_StartsTheShellThere()
    {
        using TempFolder first = new();
        using TempFolder second = new();
        PromptSession session = Session(Record() with { WorkspacePath = first.Path, WorkingFolder = first.Path });
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(() => Server().Object);
        Plain();
        session.Configuration.IsShellAllowed = true;
        await session.Messages("one");

        await session.Move(second.Path);
        await session.Messages("two");

        libraryTools.Verify(mock => mock.CreateShell(first.Path), Times.Once);
        libraryTools.Verify(mock => mock.CreateShell(second.Path), Times.Once);
    }

    [Fact]
    public async Task Send_SaysTheAiHasFileToolsEvenWithoutAWorkspace()
    {
        PromptSession session = LibrarySession();
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();

        await session.Messages("one");

        Served("read_file");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles())), Times.Once);
    }

    [Fact]
    public async Task Tools_DoNotListTheLibraryFileToolsWhichAreServedInTheBackground()
    {
        PromptSession session = LibrarySession();
        Library(Tool("read_file"), Tool("write_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();
        SetTools(session, Tool("mine"));

        await session.Messages("one");

        Assert.Equal(["mine"], session.Transient.Tools.Select(tool => tool.Name));
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
        Assert.All(seen, file => Assert.StartsWith(Path.GetTempPath(), file));
        Assert.All(seen, file => Assert.DoesNotContain(temp.Path, file));
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
        toolHost.Verify(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>()), Times.Never);
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

        Assert.False(session.Configuration.IsShellAllowed);
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
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(server.Object);
        Plain();
        session.Configuration.IsShellAllowed = true;
        session.Transient.Tools.Add(Tool("mine"));

        await session.Messages("one");
        await session.Messages("two");

        Served("read_file", "shell", "mine");
        libraryTools.Verify(mock => mock.CreateShell(record.WorkingFolder), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles() && start.HasShell())), Times.Exactly(2));
        Assert.Equal(["mine"], session.Transient.Tools.Select(tool => tool.Name));
        Assert.True(session.Configuration.IsShellAllowed);
    }

    [Fact]
    public async Task IsWebAllowed_IsOffByDefaultAndOffersNoWebTool()
    {
        PromptSession session = Session(Record());
        engine.Setup(mock => mock.Accepts("image/png")).Returns(false);
        Library(Tool("read_file"));
        Plain();

        await session.Messages("one");

        Assert.False(session.Configuration.IsWebAllowed);
        Served("read_file");
        libraryTools.Verify(mock => mock.CreateFetch(), Times.Never);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task TheImageTool_IsAlwaysOfferedToAnEngineThatCanBeShownImagesAndToNoOtherOne(bool engineTakesImages, bool offered)
    {
        PromptSession session = Session(Record());
        engine.Setup(mock => mock.Accepts("image/png")).Returns(engineTakesImages);
        libraryTools.Setup(mock => mock.CreateImage(It.IsAny<IStore>(), It.IsAny<Func<string, PromptAttachment, CancellationToken, Task>>())).Returns(() => Tool("view_image"));
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();

        await session.Messages("one");

        string[] expected = [.. new[] { "read_file" }, .. offered ? ["view_image"] : Array.Empty<string>()];
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(expected.Append("respond")))), Times.Once);
    }

    [Fact]
    public async Task ImageTool_GivesTheImageToTheAiAsAnInputOfItsOwnWithTheImageAttachedAndANote()
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
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Answers();
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
    public async Task ImageTool_DuringARequest_SteersTheImageIntoTheWorkOfTheAiInsteadOfRefusingIt()
    {
        PromptSession session = Persistent();
        Func<string, PromptAttachment, CancellationToken, Task>? give = null;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<PromptInput> steered = [];
        engine.Setup(mock => mock.Accepts("image/png")).Returns(true);
        libraryTools.Setup(mock => mock.CreateImage(It.IsAny<IStore>(), It.IsAny<Func<string, PromptAttachment, CancellationToken, Task>>())).Callback<IStore, Func<string, PromptAttachment, CancellationToken, Task>>((_, given) => give = given).Returns(() => Tool("view_image"));
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => started.TrySetResult()));
        process.Setup(mock => mock.Steer(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Callback<PromptInput, CancellationToken>((input, _) => steered.Add(input)).ReturnsAsync(true);
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
    public async Task ImageTool_DuringARequestOfAnEngineThatCannotSteer_WaitsAndIsGivenAsWorkOnceTheRequestIsOver()
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
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => started.TrySetResult()));
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
    public async Task EnableWeb_OffersTheFetchAndSearchToolsEvenWithoutFilesAndOnlyWhileItIsOn()
    {
        PromptSession session = Session(Record());
        libraryTools.Setup(mock => mock.CreateFetch()).Returns(() => Tool("fetch_url"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();
        session.Configuration.IsWebAllowed = true;

        await session.Messages("one");
        session.Configuration.IsWebAllowed = false;
        await session.Messages("two");

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(new[] { "fetch_url", "search_web", "respond" }))), Times.Once);
        libraryTools.Verify(mock => mock.CreateFetch(), Times.Once);
    }

    [Fact]
    public async Task TheTimeTheSearchOfToolsAndTheDelegationAreAlwaysOffered()
    {
        PromptSession session = Session(Record());
        Plain();

        await session.Messages("one");

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).SequenceEqual(new[] { "current_time", "tool_search", "delegate", "respond" }))), Times.Once);
    }

    [Fact]
    public async Task ImageGeneration_OffersTheImageToolWhileTheSessionAllowsItAndTheEnvironmentHasASetup()
    {
        FakeImageGenerator generator = new();
        libraryTools.Setup(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), generator)).Returns(() => Tool("generate_image"));
        Library(Tool("read_file"));
        Plain();
        PromptImageSetup setup = new LocalImageSetup { Models = PromptCapabilityModels.All("checkpoint") };
        PromptSession session = Session(Record(), generators: _ => generator, images: () => setup);
        session.Configuration.IsImageGenerationAllowed = true;

        await session.Messages("one");

        Served("read_file", "generate_image");
        libraryTools.Verify(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), generator), Times.Once);
    }

    [Fact]
    public async Task ImageGeneration_WithoutTheSwitchOrWithoutASetupInTheEnvironment_OffersNoImageTool()
    {
        libraryTools.Setup(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), It.IsAny<IPromptImageGenerator>())).Returns(() => Tool("generate_image"));
        Library(Tool("read_file"));
        Plain();
        PromptImageSetup setup = new LocalImageSetup { Models = PromptCapabilityModels.All("checkpoint") };
        PromptSession notAllowed = Session(Record(), generators: _ => new FakeImageGenerator(), images: () => setup);
        PromptSession noSetup = Session(Record(), generators: _ => new FakeImageGenerator());
        noSetup.Configuration.IsImageGenerationAllowed = true;

        await notAllowed.Messages("one");
        await noSetup.Messages("one");

        libraryTools.Verify(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), It.IsAny<IPromptImageGenerator>()), Times.Never);
    }

    [Fact]
    public async Task ImageGeneration_WithASetupOfTheEnvironmentThatIsNotValid_FailsOnlyTheSessionThatAllowsIt()
    {
        libraryTools.Setup(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), It.IsAny<IPromptImageGenerator>())).Returns(() => Tool("generate_image"));
        Library(Tool("read_file"));
        Plain();
        PromptImageSetup broken = new OpenAiImageSetup { ApiKey = " " };
        PromptSession allowed = Session(Record(), generators: _ => new FakeImageGenerator(), images: () => broken);
        allowed.Configuration.IsImageGenerationAllowed = true;
        PromptSession notAllowed = Session(Record(), generators: _ => new FakeImageGenerator(), images: () => broken);

        PromptSessionException failure = await Assert.ThrowsAsync<PromptSessionException>(() => allowed.Messages("one"));
        await notAllowed.Messages("one");

        Assert.Contains("image generation of the environment cannot be used", failure.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<ArgumentException>(failure.InnerException);
    }

    [Fact]
    public async Task ImageGeneration_ChangedInTheEnvironmentOrSwitchedOffBetweenPrompts_ReplacesOrRemovesTheTool()
    {
        FakeImageGenerator first = new();
        FakeImageGenerator second = new();
        libraryTools.Setup(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), It.IsAny<IPromptImageGenerator>())).Returns(() => Tool("generate_image"));
        Library(Tool("read_file"));
        Plain();
        PromptImageSetup setup = new LocalImageSetup { Models = PromptCapabilityModels.All("a") };
        PromptSession session = Session(Record(), generators: options => options is LocalImageSetup { Models.Fast: "a" } ? first : second, images: () => setup);
        session.Configuration.IsImageGenerationAllowed = true;
        await session.Messages("one");

        setup = new LocalImageSetup { Models = PromptCapabilityModels.All("b") };
        await session.Messages("two");
        session.Configuration.IsImageGenerationAllowed = false;
        await session.Messages("three");

        libraryTools.Verify(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), first), Times.Once);
        libraryTools.Verify(mock => mock.CreateGenerateImage(It.IsAny<IStore>(), second), Times.Once);
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Any(tool => tool.Name == "generate_image"))), Times.Exactly(2));
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Any(tool => tool.Name == "read_file") && !started.Any(tool => tool.Name == "generate_image"))), Times.Once);
    }

    [Fact]
    public async Task EnableWeb_OffersTheLibrarysSearchBesidesFetchToAnEngineThatHasNoneOfItsOwn()
    {
        libraryTools.Setup(mock => mock.CreateFetch()).Returns(() => Tool("fetch_url"));
        PromptSession session = Session(Record());
        Plain();
        session.Configuration.IsWebAllowed = true;

        await session.Messages("one");

        Served("fetch_url", "search_web");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => !start.Search)), Times.Once);
    }

    [Fact]
    public async Task EnableWeb_WithAnEngineThatSearchesItself_AllowsItsSearchAndOffersOnlyFetch()
    {
        engine.SetupGet(mock => mock.NativeSearch).Returns(true);
        libraryTools.Setup(mock => mock.CreateFetch()).Returns(() => Tool("fetch_url"));
        PromptSession session = Session(Record());
        Plain();
        session.Configuration.IsWebAllowed = true;

        await session.Messages("one");

        Served("fetch_url");
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Search)), Times.Once);
        libraryTools.Verify(mock => mock.CreateSearch(), Times.Never);
    }

    [Fact]
    public async Task EnableWeb_OffNeitherAllowsTheEnginesSearchNorOffersTheTools()
    {
        engine.SetupGet(mock => mock.NativeSearch).Returns(true);
        PromptSession session = Session(Record());
        Plain();

        await session.Messages("one");

        Assert.False(session.Configuration.IsWebAllowed);
        Served();
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => !start.Search)), Times.Once);
        libraryTools.Verify(mock => mock.CreateSearch(), Times.Never);
        libraryTools.Verify(mock => mock.CreateFetch(), Times.Never);
    }

    [Fact]
    public async Task EnableWeb_ChangedBetweenPrompts_RestartsAKeptProcessOfAnEngineWhoseSearchIsAllowedWhenItStarts()
    {
        engine.SetupGet(mock => mock.NativeSearch).Returns(true);
        libraryTools.Setup(mock => mock.CreateFetch()).Returns(() => Tool("fetch_url"));
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.IsWebAllowed = true;
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
        PromptToolServiceOptions? created = null;
        provider.Setup(mock => mock.Create(It.IsAny<PromptToolServiceOptions?>(), It.IsAny<CancellationToken>())).Callback<PromptToolServiceOptions?, CancellationToken>((options, _) => created = options).ReturnsAsync(helper);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "the helper found it"), Event(PromptEngineEventKind.Finished)));
        session.Configuration.Instructions = "Be brief.";
        await session.Messages("start");

        string answer = await run!("look into x", false, CancellationToken.None);

        Assert.Equal("the helper found it", answer);
        Assert.Equal((PromptCapability.Balanced, "/work"), (session.Configuration.Capability, created!.WorkspacePath));
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
        provider.Setup(mock => mock.Create(It.IsAny<PromptToolServiceOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(helper);
        await session.Messages("start");

        await run!("task", false, CancellationToken.None);

        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Any(tool => tool.Name == "delegate"))), Times.Once);
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => !started.Any(tool => tool.Name == "delegate"))), Times.Once);
    }

    [Fact]
    public async Task Delegate_WithTheContextShared_RunsTheTaskInAForkOfTheConversationThatIsThrownAway()
    {
        Func<string, bool, CancellationToken, Task<string>>? run = null;
        libraryTools.Setup(mock => mock.CreateDelegate(It.IsAny<Func<string, bool, CancellationToken, Task<string>>>())).Callback<Func<string, bool, CancellationToken, Task<string>>>(given => run = given).Returns(() => Tool("delegate"));
        List<PromptInput> turns = Turns();
        PromptSession session = Session(Record());
        PromptSession fork = Session(Record());
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(fork);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Reply, "forked answer"), Event(PromptEngineEventKind.Finished)));
        await session.Messages("start");

        string answer = await run!("review the plan", true, CancellationToken.None);

        Assert.Equal("forked answer", answer);
        Assert.Contains("review the plan", turns[^1].Text, StringComparison.Ordinal);
        Assert.Contains("The request the other AI is working on now:\nstart", turns[^1].Text, StringComparison.Ordinal);
        Assert.Contains("continue its conversation", turns[^1].Text, StringComparison.Ordinal);
        provider.Verify(mock => mock.Delete(fork.Id, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(mock => mock.Create(It.IsAny<PromptToolServiceOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delegate_WhenTheHelperFails_FailsTheCallWithItsReasonAndStillRemovesTheHelper()
    {
        Func<string, bool, CancellationToken, Task<string>>? run = null;
        libraryTools.Setup(mock => mock.CreateDelegate(It.IsAny<Func<string, bool, CancellationToken, Task<string>>>())).Callback<Func<string, bool, CancellationToken, Task<string>>>(given => run = given).Returns(() => Tool("delegate"));
        Plain();
        PromptSession session = Session(Record());
        PromptSession helper = Session(Record());
        provider.Setup(mock => mock.Create(It.IsAny<PromptToolServiceOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(helper);
        await session.Messages("start");
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events(Event(PromptEngineEventKind.Failed, "the helper broke")));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => run!("task", false, CancellationToken.None));

        Assert.Equal("the helper broke", exception.Message);
        provider.Verify(mock => mock.Delete(helper.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnableShell_TurnedOffAgain_ReplacesTheServerWithoutTheShell()
    {
        PromptSession session = Session(Record());
        Mock<IPromptToolServer> first = Server("http://127.0.0.1:1/mcp");
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(first.Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        Plain();
        session.Configuration.IsShellAllowed = true;
        session.Transient.Tools.Add(Tool("mine"));
        await session.Messages("one");

        session.Configuration.IsShellAllowed = false;
        await session.Messages("two");

        first.Verify(mock => mock.DisposeAsync(), Times.Once);
        toolHost.Verify(mock => mock.Start(It.Is<IReadOnlyList<PromptTool>>(started => started.Select(tool => tool.Name).Where(name => !everyday.Contains(name)).SequenceEqual(new[] { "mine", "respond" }))), Times.Once);
    }

    [Fact]
    public async Task EnableShell_WhenPersistentAndChanged_StartsANewProcess()
    {
        PromptSession session = Persistent();
        toolHost.SetupSequence(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server("http://127.0.0.1:1/mcp").Object).Returns(Server("http://127.0.0.1:2/mcp").Object);
        session.Configuration.IsShellAllowed = true;
        await session.Messages("one");

        session.Configuration.IsShellAllowed = false;
        session.Transient.Tools.Add(Tool("mine"));
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
    }

    [Fact]
    public void Tools_RejectTheNameOfTheShellTool()
    {
        PromptSession session = Session(Record());

        Assert.Throws<ArgumentException>(() => session.Transient.Tools.Add(Tool("shell")));
    }

    [Fact]
    public async Task Send_WithMounts_SaysTheAiHasFiles()
    {
        PromptSession session = LibrarySession();
        Library(Tool("read_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();

        await session.Messages("one");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.ApiKey == null && start.Schema == null && start.HasEndpoint() && start.HasFiles())), Times.Once);
    }

    [Fact]
    public async Task Send_WithLibraryTools_OffersThemWithTheCallersToolsAndBuildsThemOnce()
    {
        PromptSession session = LibrarySession();
        Library(Tool("read_file"), Tool("write_file"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        Plain();
        SetTools(session, Tool("mine"));

        await session.Messages("one");
        await session.Messages("two");

        Served("read_file", "write_file", "mine");
        libraryTools.Verify(mock => mock.Create(It.IsAny<IStore>()), Times.Once);
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
    public void Provider_IsTheOneOfTheEngine()
    {
        engine.SetupGet(mock => mock.Provider).Returns(PromptProvider.Local);

        Assert.Equal(PromptProvider.Local, Session(Record()).Provider);
    }

    [Fact]
    public async Task Settings_AfterDisposal_RefuseChanges()
    {
        PromptSession session = Session(Record());
        await session.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => session.Configuration.Effort = PromptEffort.High);
        Assert.Throws<ObjectDisposedException>(() => session.Transient.Tools.Add(Tool("late")));
        Assert.Equal(PromptEffort.High, session.Configuration.Effort);
    }

    [Fact]
    public void Settings_ThatAreNotAllowedThrowAtOnceAndChangeNothing()
    {
        PromptSession session = Session(Record());

        Assert.Throws<ArgumentException>(() => session.Transient.Tools.Add(Tool("bad name")));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Configuration.Effort = (PromptEffort)99);

        Assert.Equal(PromptEffort.High, session.Configuration.Effort);
        Assert.Empty(session.Transient.Tools);
    }

    [Fact]
    public async Task SeveralChangesBeforeAPrompt_CostOneRestartOfAKeptProcess()
    {
        PromptSession session = Persistent();
        await session.Messages("one");

        session.Configuration.Effort = PromptEffort.High;
        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Instructions = "Be brief.";
        await session.Messages("two");

        runner.Verify(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>()), Times.Exactly(2));
        engine.Verify(mock => mock.CreatePersistentStart(It.Is<PromptEngineStart>(start => start.Model == "opus" && start.Settings["effort"] == "high" && start.Instructions.EndsWith("Be brief.") && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Once);
    }

    [Fact]
    public async Task AChangeWhileAPromptRuns_DoesNotReachThatPromptAndSettingsAreTakenTogetherAtTheNext()
    {
        PromptSession session = Session(Record());
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Blocked(release.Task, () => { }));
        Task<List<string>> first = session.Messages("one");
        await Task.Delay(100);

        session.Configuration.Capability = PromptCapability.Strong;
        session.Transient.Tools.Add(Tool("late"));
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
        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Effort = PromptEffort.Low;

        await session.Messages("one");
        await session.Messages("two");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Model == "opus" && start.Settings["effort"] == "low" && start.ApiKey == null && start.Schema == null && start.OffersOnlyRespond())), Times.Exactly(2));
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Capability == PromptCapability.Strong && link.Effort == PromptEffort.Low), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ANewSession_StartsWithTheDefaultSettings()
    {
        PromptSession session = Session(Record());
        IPromptSessionConfiguration configuration = session.Configuration;

        Assert.Equal((PromptCapability.Balanced, PromptEffort.High, false), (configuration.Capability, configuration.Effort, configuration.IsShellAllowed));
        Assert.Empty(session.Transient.Tools);
        Assert.Empty(session.Transient.McpServers);
        Assert.Null(session.Transient.Asker);
        Assert.Null(configuration.Instructions);
    }

    [Fact]
    public async Task TheConfigurationIsStoredWhenAPromptStartsButNeitherToolsNorServersAre()
    {
        PromptSession graceful = Session(Record());
        PromptSession immediate = Session(Record());
        Plain();
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Returns(Server().Object);
        graceful.Configuration.IsShellAllowed = true;
        graceful.Configuration.Instructions = "Be brief.";
        graceful.Transient.McpServers["db"] = PromptMcpServer.Http("https://example.com");
        graceful.Transient.Tools.Add(Tool("mine"));
        immediate.Configuration.IsShellAllowed = true;

        await graceful.Messages("one");
        immediate.Dispose();
        await immediate.DisposeAsync();
        await graceful.DisposeAsync();

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Id.ToString("N") == graceful.Id && link.IsShellAllowed && link.Instructions == "Be brief."), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Id.ToString("N") == immediate.Id), It.IsAny<CancellationToken>()), Times.Never);
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
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptToolService>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>())).Returns(branch);
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

        await session.Send("go", new PromptSendOptions { Notes = ["first"] });
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

        await session.Send("go", new PromptSendOptions { Notes = ["one", "two", "three"] });
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

        await session.Send("go", new PromptSendOptions { Notes = ["keep me"] });
        await Idle(session);
        await session.Send("go", new PromptSendOptions { Notes = ["and me"] });
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
        => await Assert.ThrowsAsync<ArgumentException>(() => Session(Record()).Send("go", new PromptSendOptions { Notes = [note] }));

    [Fact]
    public async Task Send_AfterDisposal_Throws()
    {
        PromptSession session = Session(Record());
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Send("late", new PromptSendOptions { Notes = ["late"] }));
    }
}
