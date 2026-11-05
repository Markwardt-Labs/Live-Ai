namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionModesTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<IPromptProvider> provider = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptProcess> process = new();

    private readonly Mock<IPromptToolHost> toolHost = new();

    private readonly Mock<IPromptLibraryTools> libraryTools = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    public PromptSessionModesTests()
    {
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.Plain(It.IsAny<PromptInput>())).Returns((PromptInput input) => input.Text);
        engine.Setup(mock => mock.CreateStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("tool"));
        libraryTools.SetupGet(mock => mock.Names).Returns(["read_file"]);
        libraryTools.SetupGet(mock => mock.ShellName).Returns("shell");
        libraryTools.OfferTheEverydayTools();
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns([]);
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(() => Server().Object);
    }

    private PromptSession Session(string? aiSessionId = null, ILogger? logger = null)
        => new(new PromptSessionRecordBuilder { AiSessionId = aiSessionId }.Build(engine.Object), null, store.Object, provider.Object, engine.Object, runner.Object, toolHost.Object, libraryTools.Object, opener.Object, logger);

    private Mock<IPromptToolServer> Server()
    {
        Mock<IPromptToolServer> server = new();
        server.SetupGet(mock => mock.Endpoint).Returns(new ToolEndpoint { Url = "http://127.0.0.1:1/mcp", Token = "t", Names = [] });
        return server;
    }

    private PromptEngineEvent Event(PromptEngineEventKind kind, string text = "")
        => new() { Kind = kind, Text = text };

    private async IAsyncEnumerable<PromptEngineEvent> Replying(string text)
    {
        await Task.Yield();
        yield return Event(PromptEngineEventKind.Reply, text);
    }

    private async IAsyncEnumerable<PromptEngineEvent> Blocked(Task release, Action started, string text = "done")
    {
        started();
        await release;
        yield return Event(PromptEngineEventKind.Reply, text);
    }

    private void Conversing(Func<IAsyncEnumerable<PromptEngineEvent>> work)
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        engine.Setup(mock => mock.CreatePersistentStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("tool"));
        runner.Setup(mock => mock.Open(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<PromptEngineStart>())).Returns(process.Object);
        process.SetupGet(mock => mock.IsRunning).Returns(true);
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns(work);
    }

    private async IAsyncEnumerable<PromptEngineEvent> Working(Task release, Action started)
    {
        started();
        await release;
        yield return Event(PromptEngineEventKind.Reply, "done");
        yield return Event(PromptEngineEventKind.Finished);
    }

    private async Task Eventually(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private void Answers(string prompt, Func<IAsyncEnumerable<PromptEngineEvent>> answer)
        => runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), prompt, It.IsAny<CancellationToken>())).Returns(answer);

    [Fact]
    public async Task ADirectRequest_PutsTheSessionInTheRespondingStateAndOutputReportsTheStatesAndWhatTheAiSays()
    {
        PromptSession session = Session();
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);
        Answers("hi", () => Replying("hello"));
        Assert.Equal(PromptSessionState.Waiting, session.State);

        string reply = await session.Request("hi");
        await recorder.Until(values => values.Count >= 3);

        Assert.Equal("hello", reply);
        Assert.Equal(PromptSessionState.Waiting, session.State);
        Assert.Equal([PromptSessionState.Responding, PromptSessionState.Waiting], recorder.Values.OfType<PromptStateEvent>().Select(change => change.State));
        Assert.Equal(["hello"], recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text));
        Assert.IsType<PromptStateEvent>(recorder.Values[0]);
        Assert.IsType<PromptMessageEvent>(recorder.Values[1]);
        Assert.IsType<PromptStateEvent>(recorder.Values[2]);
    }

    [Fact]
    public async Task Send_WhileWaiting_StartsWorkWhoseOutputIsOnOutput()
    {
        PromptSession session = Session();
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);
        Answers("hello", () => Replying("working on it"));

        await session.Send("hello");
        await Eventually(() => session.State == PromptSessionState.Waiting && recorder.Values.Count >= 3);

        Assert.Equal([PromptSessionState.Working, PromptSessionState.Waiting], recorder.Values.OfType<PromptStateEvent>().Select(change => change.State));
        Assert.Equal(["working on it"], recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text));
    }

    [Fact]
    public async Task Send_WhileTheAiIsWorkingOnAnEngineThatCannotTakeInputMidWork_WaitsAndGoesToTheAiWhenTheWorkEnds()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Answers("one", () => Blocked(release.Task, () => Interlocked.Increment(ref started)));
        Answers("two\n\nthree", () => Replying("both"));

        await session.Send("one");
        await Eventually(() => Volatile.Read(ref started) == 1);
        await session.Send("two");
        await session.Send("three");
        Assert.Equal(PromptSessionState.Working, session.State);
        release.SetResult();
        await Eventually(() => session.State == PromptSessionState.Waiting);

        runner.Verify(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "one", It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "two\n\nthree", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_WhileTheAiIsWorkingOnAnEngineThatTakesInputMidWork_GivesItToTheRunningProcessAtOnce()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Conversing(() => Working(release.Task, () => Interlocked.Increment(ref started)));
        process.Setup(mock => mock.Steer(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await session.Send("one");
        await Eventually(() => Volatile.Read(ref started) == 1);

        await session.Send("two");

        process.Verify(mock => mock.Steer(It.Is<PromptInput>(input => input.Text == "two"), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(PromptSessionState.Working, session.State);
        release.SetResult();
        await Eventually(() => session.State == PromptSessionState.Waiting);
        process.Verify(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_WhenTheProcessCannotTakeTheInputAnymore_WaitsAndRunsAsWorkOfItsOwn()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Conversing(() => Working(release.Task, () => Interlocked.Increment(ref started)));
        process.Setup(mock => mock.Steer(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await session.Send("one");
        await Eventually(() => Volatile.Read(ref started) == 1);

        await session.Send("two");
        release.SetResult();
        await Eventually(() => session.State == PromptSessionState.Waiting && Volatile.Read(ref started) == 2);

        process.Verify(mock => mock.Run(It.Is<PromptInput>(input => input.Text == "one"), It.IsAny<CancellationToken>()), Times.Once);
        process.Verify(mock => mock.Run(It.Is<PromptInput>(input => input.Text == "two"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_WhenTheProcessFailsToTakeTheInput_ThrowsAndDoesNotRunItLater()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Conversing(() => Working(release.Task, () => Interlocked.Increment(ref started)));
        process.Setup(mock => mock.Steer(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("broken pipe"));
        await session.Send("one");
        await Eventually(() => Volatile.Read(ref started) == 1);

        await Assert.ThrowsAsync<IOException>(() => session.Send("two", new PromptOptions { Notes = ["keep"] }));
        release.SetResult();
        await Eventually(() => session.State == PromptSessionState.Waiting);
        await session.Send("three");
        await Eventually(() => Volatile.Read(ref started) == 2);

        process.Verify(mock => mock.Run(It.Is<PromptInput>(input => input.Text == "two"), It.IsAny<CancellationToken>()), Times.Never);
        process.Verify(mock => mock.Run(It.Is<PromptInput>(input => input.Text == "three" && input.Notes.SequenceEqual(new[] { "keep" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Send_WithANoteWhileTheAiIsWorking_DoesNotRepeatTheNotesThePromptStartedWith()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        List<PromptInput> runs = [];
        List<PromptInput> steers = [];
        Conversing(() => Working(release.Task, () => Interlocked.Increment(ref started)));
        process.Setup(mock => mock.Run(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Returns((PromptInput input, CancellationToken _) =>
        {
            runs.Add(input);
            return Working(release.Task, () => Interlocked.Increment(ref started));
        });
        process.Setup(mock => mock.Steer(It.IsAny<PromptInput>(), It.IsAny<CancellationToken>())).Callback<PromptInput, CancellationToken>((input, _) => steers.Add(input)).ReturnsAsync(true);
        await session.Send("one", new PromptOptions { Notes = ["a"] });
        await Eventually(() => Volatile.Read(ref started) == 1);

        await session.Send("two", new PromptOptions { Notes = ["b"] });

        Assert.Equal(["a"], Assert.Single(runs).Notes);
        Assert.Equal(["b"], Assert.Single(steers).Notes);
        release.SetResult();
        await Eventually(() => session.State == PromptSessionState.Waiting);
    }

    [Fact]
    public async Task ASessionWithALogger_ReportsItsStatesAndTheFailuresOfItsWork()
    {
        ListLogger logger = new();
        PromptSession session = Session(logger: logger);
        Answers("hi", () => Replying("hello"));
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "bad", It.IsAny<CancellationToken>())).Returns(() => FailedTurn());

        await session.Request("hi");
        await session.Send("bad");
        await Eventually(() => logger.Entries.Any(entry => entry.Level == LogLevel.Warning));

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.EndsWith("is now Responding."));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.EndsWith("is now Waiting."));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("is now Working"));
        (LogLevel level, string message, Exception? exception) = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(session.Id, message);
        Assert.IsType<PromptSessionException>(exception);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("hello") || entry.Message.Contains("\"hi\""));
    }

    [Fact]
    public async Task Send_WhileRespondingToADirectRequest_ThrowsAStateException()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Answers("ask", () => Blocked(release.Task, () => Interlocked.Increment(ref started)));
        Task<string> asking = session.Request("ask");
        await Eventually(() => Volatile.Read(ref started) == 1);

        PromptSessionStateException exception = await Assert.ThrowsAsync<PromptSessionStateException>(() => session.Send("more"));

        Assert.Equal(PromptSessionState.Responding, exception.State);
        release.SetResult();
        await asking;
        await session.Send("now it works");
        await Eventually(() => session.State == PromptSessionState.Waiting);
    }

    [Fact]
    public async Task ADirectRequest_WhileTheAiIsWorkingOnInput_ThrowsAStateException()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Answers("work", () => Blocked(release.Task, () => Interlocked.Increment(ref started)));
        await session.Send("work");
        await Eventually(() => Volatile.Read(ref started) == 1);

        PromptSessionStateException exception = await Assert.ThrowsAsync<PromptSessionStateException>(() => session.Request("ask"));

        Assert.Equal(PromptSessionState.Working, exception.State);
        release.SetResult();
        await Eventually(() => session.State == PromptSessionState.Waiting);
    }

    [Fact]
    public async Task WorkThatFails_IsReportedAsAFailureEventAndTheSessionGoesOnWaiting()
    {
        PromptSession session = Session();
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "bad", It.IsAny<CancellationToken>())).Returns(() => FailedTurn());
        Answers("good", () => Replying("fine"));

        await session.Send("bad");
        await Eventually(() => session.State == PromptSessionState.Waiting);
        await session.Send("good");
        await Eventually(() => recorder.Values.OfType<PromptMessageEvent>().Any());

        Assert.Equal("broke", Assert.Single(recorder.Values.OfType<PromptFailureEvent>()).Message);
        Assert.Equal(["fine"], recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text));
    }

    [Fact]
    public async Task ADirectRequestThatFails_ThrowsAndReportsAFailureEvent()
    {
        PromptSession session = Session();
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), "bad", It.IsAny<CancellationToken>())).Returns(() => FailedTurn());

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("bad"));
        await recorder.Until(values => values.OfType<PromptFailureEvent>().Any());

        Assert.Equal("broke", exception.Message);
        Assert.Equal("broke", Assert.Single(recorder.Values.OfType<PromptFailureEvent>()).Message);
        Assert.Equal(PromptSessionState.Waiting, session.State);
    }

    [Fact]
    public async Task Output_CompletesWhenTheSessionIsDisposed()
    {
        PromptSession session = Session();
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);

        await session.DisposeAsync();
        await recorder.Ended();

        Assert.True(recorder.IsCompleted);
    }

    [Fact]
    public async Task Output_DoesNotReplayAnythingToAWatcherThatArrivesLate()
    {
        PromptSession session = Session();
        Answers("hi", () => Replying("hello"));
        await session.Request("hi");
        Recorder<PromptEvent> late = new();

        session.Output.Subscribe(late);

        Assert.Empty(late.Values);
    }

    [Fact]
    public async Task AForkRequest_IsAskedInACopyThatIsThrownAwayAndTheMainSessionIsNotTouched()
    {
        PromptSession session = Session("origin");
        PromptSession fork = Session();
        engine.Setup(mock => mock.Branch(It.IsAny<PromptEngineStart>(), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { ForkedFrom = "origin" });
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>())).Returns(fork);
        Answers("side question", () => Replying("side answer"));
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);

        string reply = await session.Request("side question", new PromptRequestOptions { Mode = PromptRequestMode.Fork });

        Assert.Equal("side answer", reply);
        Assert.Empty(recorder.Values);
        Assert.Equal(PromptSessionState.Waiting, session.State);
        provider.Verify(mock => mock.Delete(fork.Id, It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fork.Send("late"));
    }

    [Fact]
    public async Task AForkRequest_CanBeAskedWhileTheMainSessionIsWorkingAndWhileItIsResponding()
    {
        PromptSession session = Session("origin");
        engine.Setup(mock => mock.Branch(It.IsAny<PromptEngineStart>(), It.IsAny<PromptHistoryPoint?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromptBranch { ForkedFrom = "origin" });
        opener.SetupSequence(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>())).Returns(Session()).Returns(Session());
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Answers("work", () => Blocked(release.Task, () => Interlocked.Increment(ref started)));
        Answers("quick", () => Replying("answer"));
        await session.Send("work");
        await Eventually(() => Volatile.Read(ref started) == 1);

        Assert.Equal("answer", await session.Request("quick", new PromptRequestOptions { Mode = PromptRequestMode.Fork }));
        Assert.Equal(PromptSessionState.Working, session.State);
        release.SetResult();
        await Eventually(() => session.State == PromptSessionState.Waiting);
    }

    [Fact]
    public async Task AMixedRequest_IsGivenToTheAiAsInputAndAnsweredThroughTheRespondToolWithItsUniqueIdentifier()
    {
        PromptSession session = Session();
        IReadOnlyList<PromptTool>? offered = null;
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Callback<IReadOnlyList<PromptTool>, IPromptToolHooks?>((tools, _) => offered = tools).Returns(Server().Object);
        string? seen = null;
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((IPromptEngine _, ProcessStartInfo _, string text, CancellationToken token) =>
        {
            seen = text;
            return CallingRespond(offered!, text, token);
        });

        Answer answer = await session.Request<Answer>("rate it", new PromptRequestOptions { Mode = PromptRequestMode.Mixed });

        Assert.Equal(new Answer("fine", 4), answer);
        Assert.Contains("rate it", seen);
        Assert.Matches("Request r[0-9a-f]{12}:", seen);
        Assert.Contains("respond", seen);
        Assert.Equal(["respond"], offered!.Select(tool => tool.Name).Where(name => name is not ("current_time" or "tool_search" or "delegate")));
        Assert.Equal(PromptSessionState.Waiting, session.State);
    }

    [Fact]
    public async Task AMixedRequest_ThatTheAiDoesNotAnswerInItsWork_Fails()
    {
        PromptSession session = Session();
        Answers("question", () => Replying("I will think about it"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(Server().Object);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.Is<string>(text => text.Contains("question")), It.IsAny<CancellationToken>())).Returns(() => Replying("I will think about it"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("question", new PromptRequestOptions { Mode = PromptRequestMode.Mixed }));

        Assert.Contains("without answering", exception.Message);
    }

    [Fact]
    public async Task TheRespondTool_RefusesAnAnswerThatLacksAPropertyTheSchemaRequiresAndAnUnknownRequest()
    {
        PromptSession session = Session();
        IReadOnlyList<PromptTool>? offered = null;
        string? identifier = null;
        TaskCompletionSource<Exception> refused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Callback<IReadOnlyList<PromptTool>, IPromptToolHooks?>((tools, _) => offered = tools).Returns(Server().Object);
        runner.Setup(mock => mock.Run(engine.Object, It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((IPromptEngine _, ProcessStartInfo _, string text, CancellationToken token) => Refusing(offered!, text, refused, id => identifier = id, token));

        Task<Answer> asking = session.Request<Answer>("rate it", new PromptRequestOptions { Mode = PromptRequestMode.Mixed });
        Exception first = await refused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => asking);

        Assert.Contains("missing the property", first.Message);
        PromptTool respond = Assert.Single(offered!, tool => tool.Name == "respond");
        ArgumentException unknown = await Assert.ThrowsAsync<ArgumentException>(() => respond.Handler(JsonDocument.Parse("""{"request_id":"rnone","answer":{}}""").RootElement, CancellationToken.None));
        Assert.Contains("rnone", unknown.Message);
        Assert.NotNull(identifier);
    }

    [Fact]
    public async Task AMixedRequest_WhileRespondingToADirectRequest_ThrowsAStateException()
    {
        PromptSession session = Session();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Answers("ask", () => Blocked(release.Task, () => Interlocked.Increment(ref started)));
        Task<string> asking = session.Request("ask");
        await Eventually(() => Volatile.Read(ref started) == 1);

        await Assert.ThrowsAsync<PromptSessionStateException>(() => session.Request("mixed", new PromptRequestOptions { Mode = PromptRequestMode.Mixed }));

        release.SetResult();
        await asking;
    }

    [Fact]
    public async Task Request_UndefinedMode_IsRefused()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Session().Request("x", new PromptRequestOptions { Mode = (PromptRequestMode)9 }));

    private async IAsyncEnumerable<PromptEngineEvent> FailedTurn()
    {
        await Task.Yield();
        yield return Event(PromptEngineEventKind.Failed, "broke");
    }

    private async IAsyncEnumerable<PromptEngineEvent> CallingRespond(IReadOnlyList<PromptTool> tools, string text, [EnumeratorCancellation] CancellationToken cancellation)
    {
        string id = System.Text.RegularExpressions.Regex.Match(text, "Request (r[0-9a-f]{12}):").Groups[1].Value;
        PromptTool respond = tools.Single(tool => tool.Name == "respond");
        await respond.Handler(JsonDocument.Parse($$$"""{"request_id":"{{{id}}}","answer":{"summary":"fine","score":4}}""").RootElement, cancellation);
        yield return Event(PromptEngineEventKind.Reply, "carrying on with my work");
    }

    private async IAsyncEnumerable<PromptEngineEvent> Refusing(IReadOnlyList<PromptTool> tools, string text, TaskCompletionSource<Exception> refused, Action<string> found, [EnumeratorCancellation] CancellationToken cancellation)
    {
        string id = System.Text.RegularExpressions.Regex.Match(text, "Request (r[0-9a-f]{12}):").Groups[1].Value;
        found(id);

        try
        {
            await tools.Single(tool => tool.Name == "respond").Handler(JsonDocument.Parse($$$"""{"request_id":"{{{id}}}","answer":{"score":4}}""").RootElement, cancellation);
        }
        catch (ArgumentException exception)
        {
            refused.TrySetResult(exception);
        }

        yield return Event(PromptEngineEventKind.Reply, "could not answer");
    }
}
