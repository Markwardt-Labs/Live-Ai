namespace Markwardt.LiveAi.Tests.Unit.Testing;

public sealed class FakePromptSessionTests
{
    private FakePromptSession Session()
        => new();

    [Fact]
    public async Task RepliesAreGivenInTheOrderTheyWereScripted()
    {
        FakePromptSession session = Session().Reply("one").Reply("two");

        Assert.Equal("one", await session.Request("a"));
        Assert.Equal("two", await session.Request("b"));
    }

    [Fact]
    public async Task Descriptors_NameTheFakeProviderAndFollowTheCapabilityAndEffortAtEachPrompt()
    {
        FakePromptSession session = Session().Reply("a").Reply("b").Reply("c");
        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);
        Assert.Equal(new Dictionary<string, string> { ["Provider"] = "Fake" }, session.Descriptors);

        await session.Request("one");
        session.Configuration.Capability = PromptCapability.Max;
        await session.Request("two");
        await session.Request("three");
        await recorder.Until(values => values.OfType<PromptDescriptorsEvent>().Count() == 2);

        Assert.Equal(new Dictionary<string, string> { ["Provider"] = "Fake", ["Model"] = "Fake Max", ["Effort"] = "High" }, session.Descriptors);
        Assert.Equal(["Fake Balanced", "Fake Max"], recorder.Values.OfType<PromptDescriptorsEvent>().Select(change => change.Descriptors["Model"]));
    }

    [Fact]
    public async Task TheEventsReportTheStatesAndTheEventsOfADirectRequest()
    {
        FakePromptSession session = Session().Reply("hello");
        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);

        Assert.Equal("hello", await session.Request("a"));
        await recorder.Until(values => values.OfType<PromptStateEvent>().Count() == 2);

        Assert.Equal(PromptSessionState.Waiting, session.State);
        Assert.Equal([PromptSessionState.Responding, PromptSessionState.Waiting], recorder.Values.OfType<PromptStateEvent>().Select(change => change.State));
        Assert.Equal(["hello"], recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text));
    }

    [Fact]
    public async Task SendRunsAScriptedPromptWhoseEventsAreReportedAndIsRefusedWhileResponding()
    {
        FakePromptSession session = Session().Reply("one").Hang();
        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);

        await session.Send("input");
        await Eventually(() => session.State == PromptSessionState.Waiting);
        using CancellationTokenSource stop = new();
        Task<string> responding = session.Request("ask", null, cancellation: stop.Token);
        await Eventually(() => session.State == PromptSessionState.Responding);

        await Assert.ThrowsAsync<PromptSessionStateException>(() => session.Send("more"));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responding);
        Assert.Equal(["input"], session.Prompts.Take(1).Select(prompt => prompt.Text));
        Assert.Equal(["one"], recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text));
    }

    [Fact]
    public async Task SendWhileTheSessionIsWorkingGoesIntoTheRunningPromptAtOnce()
    {
        FakePromptSession session = Session().Hang();
        await session.Send("work");
        await Eventually(() => session.State == PromptSessionState.Working && session.Prompts.Count == 1);

        await session.Send("and this too");

        Assert.Equal(["and this too"], session.Prompts.Single().Steered);
        Assert.Single(session.Prompts);
        session.Dispose();
    }

    [Fact]
    public async Task ADirectRequestWhileTheSessionIsWorking_Throws()
    {
        FakePromptSession session = Session().Hang();
        await session.Send("work");
        await Eventually(() => session.State == PromptSessionState.Working && session.Prompts.Count == 1);

        PromptSessionStateException exception = await Assert.ThrowsAsync<PromptSessionStateException>(() => session.Request("ask"));

        Assert.Equal(PromptSessionState.Working, exception.State);
        session.Dispose();
    }

    [Fact]
    public async Task AMixedRequestIsAnsweredWithTheLastMessageOfTheWorkItIsMixedInto()
    {
        FakePromptSession session = Session().Reply(new Answer("fine", 2));

        Answer answer = await session.Request<Answer>("rate", new PromptRequestOptions { Mode = PromptRequestMode.Mixed });

        Assert.Equal(new Answer("fine", 2), answer);
        Assert.NotNull(session.Prompts.Single().Schema);
    }

    [Fact]
    public async Task AForkRequestIsAskedInACopyAndLeavesTheSessionAlone()
    {
        FakePromptSession session = Session().Reply("side answer");
        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);

        Assert.Equal("side answer", await session.Request("side", new PromptRequestOptions { Mode = PromptRequestMode.Fork }));

        Assert.Empty(session.Prompts);
        Assert.Empty(recorder.Values);
        Assert.Equal(PromptSessionState.Waiting, session.State);
    }

    [Fact]
    public async Task TheEventsCompleteWhenTheSessionIsDisposed()
    {
        FakePromptSession session = Session();
        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);

        await session.DisposeAsync();
        await recorder.Ended();

        Assert.True(recorder.IsCompleted);
    }

    private async Task Eventually(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task AnObjectIsAnsweredAsJsonWhichIsWhatAStructuredAnswerIs()
    {
        FakePromptSession session = Session().Reply(new Answer("fine", 3));

        Answer answer = await session.Request<Answer>("rate");

        Assert.Equal(new Answer("fine", 3), answer);
        Assert.NotNull(session.Prompts.Single().Schema);
    }

    [Fact]
    public async Task EventsAreReportedAsScripted()
    {
        PromptEvent call = new PromptToolCallEvent { Id = "1", Name = "read_file", Server = "liveai" };
        PromptEvent usage = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 5 } };
        FakePromptSession session = Session().Reply(new PromptMessageEvent { Text = "hi" }, call, usage).Reply(call);

        List<PromptEvent> events = await session.EventList("go");
        List<PromptEvent> single = await session.EventList("go");

        Assert.Equal<PromptEvent>([new PromptMessageEvent { Text = "hi" }, call, usage], events);
        Assert.Equal([call], single);
    }

    [Fact]
    public async Task UsageAddsUpOverThePrompts()
    {
        PromptEvent usage = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 60, OutputTokens = 40 } };
        FakePromptSession session = Session().ReplyWith(_ => [new PromptMessageEvent { Text = "ok" }, usage]);

        await session.Request("one");
        await session.Request("two");

        Assert.Equal(200, session.Usage.InputTokens + session.Usage.OutputTokens);
        Assert.Equal(2, session.Prompts.Count);
    }

    [Fact]
    public async Task EveryPromptIsRecordedWithWhatTheRequestAskedForAndTheSessionsSettings()
    {
        FakePromptSession session = Session().Reply("ok");
        PromptAttachment file = new() { Name = "a.png", MediaType = "image/png", Content = new byte[] { 1 } };
        session.Configuration.Effort = PromptEffort.High;

        await session.Request("look\n\nmore", new PromptRequestOptions { Attachments = [file] });
        FakePrompt prompt = session.Prompts.Single();

        Assert.Equal("look\n\nmore", prompt.Text);
        Assert.Equal([file], prompt.Attachments);
        Assert.Equal((PromptCapability.Balanced, PromptEffort.High), (prompt.Capability, prompt.Effort));
        Assert.True(DateTimeOffset.UtcNow - prompt.Time < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task APromptWithoutAScriptedAnswerFailsAndSaysWhichPrompt()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Session().Request("unscripted question"));

        Assert.Contains("unscripted question", exception.Message);
    }

    [Fact]
    public async Task ReplyWithAnswersWhatIsNotScriptedFromThePrompt()
    {
        FakePromptSession session = Session().Reply("first").ReplyWith(prompt => [new PromptMessageEvent { Text = prompt.Text.ToUpperInvariant() }]);

        Assert.Equal("first", await session.Request("a"));
        Assert.Equal("B", await session.Request("b"));
        Assert.Equal("C", await session.Request("c"));
    }

    [Fact]
    public async Task AFailureIsThrownAndTheNextPromptGetsTheNextScriptedAnswer()
    {
        FakePromptSession session = Session().Fail("rate limited").Fail("overloaded").Reply("finally");

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));
        await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));
        string answer = await session.Request("go");

        Assert.Equal("finally", answer);
        Assert.Equal(3, session.Prompts.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => Session().Fail(new ArgumentException("bad")).Request("x"));
    }

    [Fact]
    public async Task AHangingPromptEndsWithACancellationOrAnInterruption()
    {
        FakePromptSession session = Session().Hang().Hang();

        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Request("b", null, cancellation: cancellation.Token));
        Task<List<PromptEvent>> running = session.EventList("c");
        await Task.Delay(100);
        bool interrupted = await session.Interrupt();
        List<PromptEvent> events = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(interrupted);
        Assert.Equal([new PromptInterruptedEvent()], events);
        Assert.False(await Session().Interrupt());
    }

    [Fact]
    public async Task ARequestAfterAnInterruptRunsTheNextScriptedPrompt()
    {
        FakePromptSession session = Session().Hang().Reply("new direction");
        Task<List<PromptEvent>> running = session.EventList("long");
        await Task.Delay(100);

        await session.Interrupt();
        Assert.Equal([new PromptInterruptedEvent()], await running.WaitAsync(TimeSpan.FromSeconds(5)));
        string steered = await session.Request("change");

        Assert.Equal("new direction", steered);
    }

    [Fact]
    public async Task ASessionWithAClockRecordsItsTimeOnThePrompts()
    {
        ManualTime clock = new();
        FakePromptSession session = new(time: clock);
        session.Reply("ok");
        DateTimeOffset started = clock.GetUtcNow();

        await session.Request("go");

        Assert.Equal(started, Assert.Single(session.Prompts).Time);
    }

    [Fact]
    public void ConfigureWorksLikeTheRealOneAndRejectsWhatItRejects()
    {
        FakePromptSession session = Session();
        PromptTool tool = PromptTool.Create<Question, string>("mine", "d", (_, _) => Task.FromResult("x"));

        session.Configuration.Effort = PromptEffort.Low;
        session.Configuration.Instructions = "Be brief.";
        session.Configuration.Instructions = "Be brief.";
        session.Transient.Tools.Add(tool);
        session.Transient.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");

        Assert.Equal(PromptEffort.Low, session.Configuration.Effort);
        Assert.Equal("Be brief.", session.Configuration.Instructions);
        Assert.Equal([tool], session.Transient.Tools);
        Assert.Throws<ArgumentException>(() => session.Transient.Tools.Add(PromptTool.Create<Question, string>("read_file", "d", (_, _) => Task.FromResult("x"))));
        Assert.Equal([tool], session.Transient.Tools);
    }

    [Fact]
    public async Task ASchemaThatIsNotAnObjectIsRefusedLikeInProduction()
    {
        FakePromptSession session = Session().Reply("ok");

        await Assert.ThrowsAsync<ArgumentException>(() => session.Request("x", new PromptRequestOptions { Schema = JsonDocument.Parse("""{"type":"array"}""").RootElement }));
        Assert.Equal("ok", await session.Request("fine"));
    }

    [Fact]
    public async Task AForkHasTheConversationTheConfigurationAndTheScriptButGoesItsOwnWay()
    {
        FakePromptSession session = Session().Reply("first").Reply("shared");
        session.Configuration.Instructions = "origin";
        await session.Request("one");

        FakePromptSession fork = (FakePromptSession)await session.Fork();
        fork.Reply("only fork");

        Assert.NotEqual(session.Id, fork.Id);
        Assert.Equal("origin", fork.Configuration.Instructions);
        Assert.Equal(2, (await fork.GetHistory().Take(10).ToListAsync()).Count);
        Assert.Equal("shared", await fork.Request("two"));
        Assert.Equal("shared", await session.Request("two"));
        Assert.Equal(["one", "two"], session.Prompts.Select(prompt => prompt.Text));
        Assert.Equal(["one", "two"], fork.Prompts.Select(prompt => prompt.Text));
        Assert.Equal(PromptUsageZero, fork.Usage);
    }

    private PromptUsage PromptUsageZero => new();

    [Fact]
    public async Task HistoryShowsThePromptsAndWhatTheAiDidNewestFirstAndCanBePaged()
    {
        FakePromptSession session = Session().Reply(new PromptMessageEvent { Text = "reading" }, new PromptToolCallEvent { Id = "1", Name = "read_file", Input = """{"path":"a"}""" }, new PromptToolResultEvent { Id = "1", Output = "file" }, new PromptMessageEvent { Text = "done" });
        await session.Request("go");

        IReadOnlyList<PromptHistoryEntry> all = await session.GetHistory().Take(100).ToListAsync();
        IReadOnlyList<PromptHistoryEntry> newest = await session.GetHistory().Take(2).ToListAsync();
        IReadOnlyList<PromptHistoryEntry> older = await session.GetHistory().OlderThan(newest[^1].Point).Take(100).ToListAsync();
        IReadOnlyList<PromptHistoryEntry> newer = await session.GetHistory().NewerThan(all[^1].Point).Take(100).ToListAsync();

        Assert.Equal(["done", "file", """{"path":"a"}""", "reading", "go"], all.Select(entry => entry.Text));
        Assert.Equal([typeof(PromptMessageEvent), typeof(PromptToolResultEvent), typeof(PromptToolCallEvent), typeof(PromptMessageEvent), typeof(PromptInputEvent)], all.Select(entry => entry.Event.GetType()));
        Assert.Equal("read_file", Assert.IsType<PromptToolCallEvent>(all[2].Event).Name);
        Assert.Equal(["done", "file"], newest.Select(entry => entry.Text));
        Assert.Equal(["""{"path":"a"}""", "reading", "go"], older.Select(entry => entry.Text));
        Assert.Equal(all.Take(4).Select(entry => entry.Text), newer.Select(entry => entry.Text));
        Assert.Empty(await Session().GetHistory().Take(5).ToListAsync());
    }

    [Fact]
    public async Task AMessageAndItsPromptAreTheWholeHistoryOfAShortExchange()
    {
        FakePromptSession session = Session().Reply(new PromptMessageEvent { Text = "partial" });
        await session.Request("go");

        Assert.Equal(["partial", "go"], (await session.GetHistory().Take(10).ToListAsync()).Select(entry => entry.Text));
    }

    [Fact]
    public async Task DisposingEndsTheSessionGracefullyOrAtOnce()
    {
        FakePromptSession graceful = Session().Reply("fine");
        await graceful.DisposeAsync();
        FakePromptSession immediate = Session().Hang();
        Task<string> running = immediate.Request("long");
        await Task.Delay(100);

        immediate.Dispose();

        Assert.True(graceful.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => graceful.Request("x"));
        Assert.Throws<ObjectDisposedException>(() => graceful.Configuration.Instructions = "late");
        Assert.Throws<ObjectDisposedException>(() => graceful.Transient.Tools.Add(PromptTool.Create<Question, string>("late", "d", (_, _) => Task.FromResult("x"))));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => graceful.Interrupt());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => graceful.Fork());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PromptsWaitForTheirTurnOneAtATime()
    {
        int running = 0;
        int most = 0;
        FakePromptSession session = Session().ReplyWith(_ =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
            return [new PromptMessageEvent { Text = "ok" }];
        });

        await Task.WhenAll(Enumerable.Range(0, 5).Select(number => session.Request($"p{number}")));

        Assert.Equal(1, most);
        Assert.Equal(5, session.Prompts.Count);
    }

    [Fact]
    public void TheSessionTellsItsKeyAndHasAnIdentifier()
    {
        FakePromptSession plain = new();
        FakePromptSession keyed = new(apiKey: "k");

        Assert.Null(plain.ApiKey);
        Assert.Equal("k", keyed.ApiKey);
        Assert.Matches("^[0-9a-f]{32}$", plain.Id);
    }

    private async Task<FakePromptSession> Talked()
    {
        FakePromptSession session = Session().Reply("one").Reply("two").Reply("three");
        await session.Request("first");
        await session.Request("second");
        await session.Request("third");
        return session;
    }

    [Fact]
    public async Task Fork_AtAnEntry_HoldsTheConversationThroughTheTurnOfThatEntryAndLeavesTheOriginalAlone()
    {
        FakePromptSession session = await Talked();
        IReadOnlyList<PromptHistoryEntry> history = await session.GetHistory().Take(100).ToListAsync();

        FakePromptSession fork = (FakePromptSession)await session.Fork(history.Single(entry => entry.Text == "two"));
        FakePromptSession first = (FakePromptSession)await session.Fork(history.Single(entry => entry.Text == "first"));

        Assert.Equal(["first", "one", "second", "two"], (await fork.GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text).Reverse());
        Assert.Equal(["first", "one"], (await first.GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text).Reverse());
        Assert.Equal(["first", "second"], fork.Prompts.Select(prompt => prompt.Text));
        Assert.Equal(6, (await session.GetHistory().Take(100).ToListAsync()).Count);
    }

    [Fact]
    public async Task Move_SetsTheWorkspacePathToTheFolderOrClearsItAndKeepsTheConversation()
    {
        using TempFolder folder = new();
        FakePromptSession session = await Talked();

        await session.Move(folder.Path);
        Assert.Equal(folder.Path, session.WorkspacePath);

        await session.Move(null);
        Assert.StartsWith(Path.GetTempPath(), session.WorkspacePath);
        Assert.NotEqual(folder.Path, session.WorkspacePath);
        Assert.Equal(6, (await session.GetHistory().Take(100).ToListAsync()).Count);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => session.Move(Path.Combine(folder.Path, "missing")));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Move(null));
    }

    [Fact]
    public async Task Rewind_TakesTheConversationBackToTheEndOfThatTurn()
    {
        FakePromptSession session = await Talked();
        PromptHistoryEntry second = (await session.GetHistory().Take(100).ToListAsync()).Single(entry => entry.Text == "second");

        await session.Rewind(second);

        Assert.Equal(["first", "one", "second", "two"], (await session.GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text).Reverse());
        Assert.Equal(["first", "second"], session.Prompts.Select(prompt => prompt.Text));
    }

    [Fact]
    public async Task ForkAndRewind_AtAnEntryThatIsNotInTheConversation_Fail()
    {
        FakePromptSession session = await Talked();
        PromptHistoryEntry stranger = new() { Event = new PromptInputEvent { Text = "x" }, Point = new PromptHistoryPoint(999) };

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Fork(stranger));
        await Assert.ThrowsAsync<PromptSessionException>(() => session.Rewind(stranger));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.Rewind(null!));
    }

    [Fact]
    public async Task Compact_IsCountedAndGetContextReportsWhatTheTestSet()
    {
        FakePromptSession session = Session();

        Assert.Equal(new PromptContext { Tokens = 0, Limit = 0 }, await session.GetContext());
        session.Context = new PromptContext { Tokens = 50, Limit = 200 };
        await session.Compact();
        await session.Compact();

        Assert.Equal(2, session.Compactions);
        Assert.Equal(0.25, (await session.GetContext()).Used);
    }

    [Fact]
    public async Task SendWithANoteDeliversTheNoteWithThatInputOnly()
    {
        FakePromptSession session = Session().Reply("a").Reply("b");

        await session.Send("first", new PromptSendOptions { Notes = ["one"] });
        await Eventually(() => session.State == PromptSessionState.Waiting && session.Prompts.Count == 1);
        await session.Send("second");
        await Eventually(() => session.State == PromptSessionState.Waiting && session.Prompts.Count == 2);

        Assert.Equal(["one"], session.Prompts[0].Notes);
        Assert.Empty(session.Prompts[1].Notes);
        await Assert.ThrowsAsync<ArgumentException>(() => session.Send("x", new PromptSendOptions { Notes = [" "] }));
    }

    [Fact]
    public async Task ARequestWithANote_DeliversItWithThePromptAndSendTakesRequestOptionsToo()
    {
        FakePromptSession session = Session().Reply("a").Reply("b");

        await session.Request("first", new PromptRequestOptions { Notes = ["one"] });
        await session.Send("second", new PromptRequestOptions { Notes = ["two"], Mode = PromptRequestMode.Direct });
        await Eventually(() => session.State == PromptSessionState.Waiting && session.Prompts.Count == 2);

        Assert.Equal(["one"], session.Prompts[0].Notes);
        Assert.Equal(["two"], session.Prompts[1].Notes);
    }

    [Fact]
    public async Task ANoteSentWhileThePromptRunsIsRecordedWithTheSteeredInput()
    {
        FakePromptSession session = Session().Hang();
        await session.Send("work");
        await Eventually(() => session.State == PromptSessionState.Working && session.Prompts.Count == 1);

        await session.Send("and this", new PromptSendOptions { Notes = ["the build passes"] });

        Assert.Equal(["the build passes"], session.Prompts.Single().SteeredNotes);
        session.Dispose();
    }

    [Fact]
    public async Task TheOperations_AfterDisposal_Throw()
    {
        FakePromptSession session = Session();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Compact());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetContext());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Rewind(new PromptHistoryEntry { Event = new PromptInputEvent { Text = "x" } }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Fork(new PromptHistoryEntry { Event = new PromptInputEvent { Text = "x" } }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.Send("late"));
    }

    [Fact]
    public async Task AForkOfASessionInItsAutomaticWorkspace_GetsAWorkspaceOfItsOwn()
    {
        FakePromptSession session = Session().Reply("x");

        FakePromptSession fork = (FakePromptSession)await session.Fork();

        Assert.NotEqual(session.WorkspacePath, fork.WorkspacePath);
        Assert.Contains(fork.Id, fork.WorkspacePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWorkspacePath_IsCopiedToAFork()
    {
        using TempFolder folder = new();
        FakePromptSession session = Session().Reply("x");
        await session.Move(folder.Path);

        FakePromptSession fork = (FakePromptSession)await session.Fork();

        Assert.Equal(folder.Path, fork.WorkspacePath);
    }

    [Fact]
    public async Task TheCache_IsEmptyAtFirstOwnToEachSessionAndLostWhenTheSessionIsDisposed()
    {
        FakePromptSession session = Session().Reply("x");
        await session.Cache.WriteText("a.txt", "kept");

        FakePromptSession fork = (FakePromptSession)await session.Fork();

        Assert.Equal("kept", await session.Cache.ReadText("a.txt"));
        Assert.Empty(await fork.Cache.List(string.Empty).ToListAsync());
        await session.DisposeAsync();
        Assert.Empty(await session.Cache.List(string.Empty).ToListAsync());
    }
}
