namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptRequestTests
{
    private readonly Mock<IPromptExecutor> executor = new();

    private async IAsyncEnumerable<PromptEvent> Events(params PromptEvent[] events)
    {
        foreach (PromptEvent output in events)
        {
            await Task.Yield();
            yield return output;
        }
    }

    private PromptEvent Message(string text)
        => new PromptMessageEvent { Text = text };

    private void Answers(params string[] messages)
        => executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Events([.. messages.Select(Message)]));

    private IPromptSession? session;

    private IPromptSession Session => session ??= new ExecutorSession(executor.Object).Session;

    private async Task<string> Limited(object input, PromptRequestLimits limits, CancellationToken cancellation = default)
        => (await new PromptRequester(executor.Object).Collect(input, new PromptRequestOptions(), limits, cancellation)).Text;

    private PromptAttachment Png(params byte[] content)
        => new() { Name = "a.png", MediaType = "image/png", Content = content };

    private bool IsAnswerSchema(JsonElement? schema)
        => schema is { } value && value.GetProperty("properties").TryGetProperty("score", out JsonElement _);

    private void Ran(Func<PromptRun, bool> check, Times times)
        => executor.Verify(mock => mock.Execute(It.Is<PromptRun>(run => check(run)), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Ask_SendsTheTextOnlyAndJoinsTheMessages()
    {
        Answers("one", "two");

        string text = await Session.Request("hello");

        Assert.Equal("one\n\ntwo", text);
        Ran(run => run.Text == "hello" && run.Attachments.Count == 0 && run.Schema is null, Times.Once());
    }

    [Fact]
    public async Task AnObjectAsInput_IsWrittenAsJson()
    {
        Answers("ok");

        await Session.Request(new Question("rate", ["a"], Level.Low));

        Ran(run => run.Text == "{\"task\":\"rate\",\"items\":[\"a\"],\"level\":\"Low\"}", Times.Once());
    }

    [Fact]
    public async Task Attachments_AreSentInOrder()
    {
        Answers("ok");
        PromptAttachment first = Png(1);
        PromptAttachment second = Png(2);
        PromptAttachment third = Png(3);

        await Session.Request("look", new PromptRequestOptions { Attachments = [first, second, third] });

        Ran(run => run.Attachments.SequenceEqual(new[] { first, second, third }), Times.Once());
    }

    [Fact]
    public async Task AskOfAType_SendsItsSchemaAndReadsTheAnswer()
    {
        Answers("progress", """{"summary":"fine","score":3}""");

        Answer answer = await Session.Request<Answer>("rate it");

        Assert.Equal(new Answer("fine", 3), answer);
        Ran(run => run.Text == "rate it" && IsAnswerSchema(run.Schema), Times.Once());
    }

    [Fact]
    public async Task AskWithASchemaOption_ReturnsTheAnswerAsJsonText()
    {
        JsonElement schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        Answers("""{"any":[1,2]}""");

        string answer = await Session.Request("go", new PromptRequestOptions { Schema = schema });

        Assert.Equal(2, JsonDocument.Parse(answer).RootElement.GetProperty("any").GetArrayLength());
        Ran(run => run.Schema.HasValue && run.Schema.Value.GetRawText() == schema.GetRawText(), Times.Once());
    }

    [Fact]
    public async Task TheSchemaOption_SetsTheSchemaForAStreamAndAskOfATypeReplacesIt()
    {
        Answers("""{"summary":"fine","score":3}""");
        JsonElement other = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        await Session.Messages("go", new PromptRequestOptions { Schema = PromptJson.Schema(typeof(Answer)) });
        Answer answer = await Session.Request<Answer>("go", new PromptRequestOptions { Schema = other });

        Assert.Equal(new Answer("fine", 3), answer);
        Ran(run => IsAnswerSchema(run.Schema), Times.Exactly(2));
    }

    [Fact]
    public async Task AskOfAType_ThatIsNotAnObject_Throws()
    {
        Answers("""{"value":["a","b"]}""");

        string[] answer = await Session.Request<string[]>("go");

        Assert.Equal(["a", "b"], answer);
        Ran(run => run.Schema is { } schema && schema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString() == "array", Times.Once());
    }

    [Fact]
    public async Task Stream_ReportsMessagesAsTheyComeAndPassesTheCancellationToken()
    {
        Answers("a", "b");
        using CancellationTokenSource cancellation = new();
        CancellationToken passed = default;
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) =>
        {
            passed = token;
            return Events(Message("a"), Message("b"));
        });

        List<string> messages = await Session.Messages("go", null, cancellation: cancellation.Token);

        Assert.Equal(["a", "b"], messages);
        cancellation.Cancel();
        Assert.True(passed.IsCancellationRequested);
    }

    [Fact]
    public async Task Events_ReportsEverythingInOrderAndStreamKeepsOnlyTheMessages()
    {
        PromptEvent call = new PromptToolCallEvent { Id = "1", Name = "read_file", Server = "liveai" };
        PromptEvent result = new PromptToolResultEvent { Id = "1", Output = "text" };
        PromptEvent usage = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 5 } };
        PromptEvent status = new PromptWindowsEvent { Windows = TestWindows.Claude(session: 0.04) };
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Events(Message("one"), call, result, status, Message("two"), usage));

        List<PromptEvent> events = await Session.EventList("go");
        List<string> messages = await Session.Messages("go");
        string asked = await Session.Request("go");

        Assert.Equal([Message("one"), call, result, status, Message("two"), usage], events);
        Assert.Equal(["one", "two"], messages);
        Assert.Equal("one\n\ntwo", asked);
    }

    [Fact]
    public async Task Options_CanBeUsedForManyRequests()
    {
        Answers("ok");
        PromptRequestOptions options = new() { Notes = ["a note"] };

        await Session.Request("again", options);
        await Session.Request("again", options);

        Ran(run => run.Text == "again", Times.Exactly(2));
    }

    [Fact]
    public async Task Ask_WithASchema_ReturnsOnlyTheAnswerNotTheProgressNotes()
    {
        Answers("working on it", """{"summary":"fine","score":3}""");
        JsonElement schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        string answer = await Session.Request("go", new PromptRequestOptions { Schema = schema });

        Assert.Equal("""{"summary":"fine","score":3}""", answer);
    }

    [Fact]
    public async Task AskOfAType_WhenTheAiWritesNothing_FailsWithTheNoOutputError()
    {
        Answers();

        PromptSessionOutputException exception = await Assert.ThrowsAsync<PromptSessionOutputException>(() => Session.Request<Answer>("go"));

        Assert.Contains("wrote no structured output", exception.Message);
    }

    [Fact]
    public async Task Timeout_CancelsTheWorkAndFailsWithTheTimeoutLimit()
    {
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Hang(token));

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => Limited("go", new PromptRequestLimits { Timeout = TimeSpan.FromMilliseconds(50) }));

        Assert.Equal(PromptLimit.Timeout, exception.Limit);
    }

    [Fact]
    public async Task Timeout_DoesNotTurnTheCallersCancellationIntoALimit()
    {
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Hang(token));
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Limited("go", new PromptRequestLimits { Timeout = TimeSpan.FromHours(1) }, cancellation.Token));
    }

    [Fact]
    public async Task Timeout_LetsAFastPromptFinish()
    {
        Answers("quick");

        Assert.Equal("quick", await Limited("go", new PromptRequestLimits { Timeout = TimeSpan.FromSeconds(30) }));
    }

    [Fact]
    public async Task WindowLimit_WhenReached_CancelsTheWorkAndFailsWithTheWindowLimit()
    {
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Hang(token));
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 0.91));

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => Limited("go", new PromptRequestLimits { WindowLimit = new PromptWindowLimit { Reached = status => status.Used("Session", 0.9) } }));

        Assert.Equal(PromptLimit.Window, exception.Limit);
    }

    [Fact]
    public async Task WindowLimit_ReadsTheStatusAgainOnTheIntervalUntilTheLimitIsReached()
    {
        int reads = 0;
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Hang(token));
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(() => TestWindows.Claude(session: Interlocked.Increment(ref reads) * 0.3));

        await Assert.ThrowsAsync<PromptLimitException>(() => Limited("go", new PromptRequestLimits { WindowLimit = new PromptWindowLimit { Reached = status => status.Used("Session", 0.8), Interval = TimeSpan.FromMilliseconds(20) } }));

        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task WindowLimit_WhenTheWindowsCannotBeRead_FailsWithThatError()
    {
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Hang(token));
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ThrowsAsync(new PromptSessionException("no limits"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => Limited("go", new PromptRequestLimits { WindowLimit = new PromptWindowLimit { Reached = _ => false } }));

        Assert.Equal("no limits", exception.Message);
    }

    [Fact]
    public async Task WindowLimit_ThatIsNotReached_LeavesTheAnswerAlone()
    {
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 0.01));
        Answers("done");

        Assert.Equal("done", await Limited("go", new PromptRequestLimits { WindowLimit = new PromptWindowLimit { Reached = _ => false, Interval = TimeSpan.FromMilliseconds(10) } }));
    }

    [Fact]
    public async Task LoopOfAType_AsksForTheTypeAndReadsEachAnswer()
    {
        Answers("""{"summary":"fine","score":3}""");
        await using IPromptLoop<Answer> loop = Session.Loop<Answer>("rate", PromptLoopSchedule.Manual);
        Recorder<Answer> recorder = new();
        loop.Replies.Subscribe(recorder);

        for (int attempt = 0; attempt < 500 && recorder.Values.Count == 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal([new Answer("fine", 3)], recorder.Values);
        Ran(run => IsAnswerSchema(run.Schema), Times.Once());
    }

    private void FailThenAnswer(int failures, Exception failure, params string[] messages)
    {
        int calls = 0;
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Interlocked.Increment(ref calls) <= failures ? Failing(failure) : Events([.. messages.Select(Message)]));
    }

    private async IAsyncEnumerable<PromptEvent> Failing(Exception failure)
    {
        await Task.Yield();
        throw failure;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async IAsyncEnumerable<PromptEvent> MessageThenFailure(Exception failure)
    {
        await Task.Yield();
        yield return Message("partial");
        throw failure;
    }

    private PromptRetry Quick(int attempts = 3, Func<Exception, bool>? should = null)
        => new() { MaxAttempts = attempts, InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2), ShouldRetry = should };

    [Fact]
    public async Task Retry_TriesAgainAfterAFailureOfTheAiAndReturnsTheAnswerOfTheLastTry()
    {
        FailThenAnswer(2, new PromptSessionException("rate limited"), "fine");

        Assert.Equal("fine", await Limited("go", new PromptRequestLimits { Retry = Quick() }));

        Ran(run => run.Text == "go", Times.Exactly(3));
    }

    [Fact]
    public async Task Retry_ReportsEachTryThatFailedToTheLogger()
    {
        ListLogger logger = new();
        FailThenAnswer(2, new PromptSessionException("rate limited"), "fine");

        string text = (await new PromptRequester(executor.Object, new PromptLog(logger)).Collect("go", new PromptRequestOptions(), new PromptRequestLimits { Retry = Quick() })).Text;

        Assert.Equal("fine", text);
        string[] warnings = [.. logger.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message)];
        Assert.Equal(2, warnings.Length);
        Assert.StartsWith("Try 1 of 3 failed; trying again in ", warnings[0]);
        Assert.StartsWith("Try 2 of 3 failed; trying again in ", warnings[1]);
    }

    [Fact]
    public async Task ALimitThatStopsARequest_IsReportedToTheLogger()
    {
        ListLogger logger = new();
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Hang(token));

        await Assert.ThrowsAsync<PromptLimitException>(() => new PromptRequester(executor.Object, new PromptLog(logger)).Collect("go", new PromptRequestOptions(), new PromptRequestLimits { Timeout = TimeSpan.FromMilliseconds(50) }));

        Assert.Equal("A request was stopped by its Timeout limit.", Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information).Message);
    }

    [Fact]
    public async Task Timeout_RunsOnTheClockOfTheRequester()
    {
        ManualTime clock = new();
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Hang(token));

        Task<PromptResult> asking = new PromptRequester(executor.Object, null, clock).Collect("go", new PromptRequestOptions(), new PromptRequestLimits { Timeout = TimeSpan.FromHours(1) });
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(100);
        Assert.False(asking.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(2));

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => asking.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PromptLimit.Timeout, exception.Limit);
    }

    [Fact]
    public async Task Retry_WaitsOnTheClockOfTheRequester()
    {
        ManualTime clock = new();
        FailThenAnswer(1, new PromptSessionException("rate limited"), "fine");

        Task<PromptResult> asking = new PromptRequester(executor.Object, null, clock).Collect("go", new PromptRequestOptions(), new PromptRequestLimits { Retry = new PromptRetry { MaxAttempts = 2, InitialDelay = TimeSpan.FromHours(1) } });
        await Task.Delay(100);
        Ran(_ => true, Times.Once());
        Assert.False(asking.IsCompleted);
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal("fine", (await asking.WaitAsync(TimeSpan.FromSeconds(5))).Text);
        Ran(_ => true, Times.Exactly(2));
    }

    [Fact]
    public async Task Retry_GivesUpAfterTheAttemptsAndThrowsTheLastError()
    {
        FailThenAnswer(100, new PromptSessionException("overloaded"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => Limited("go", new PromptRequestLimits { Retry = Quick(4) }));

        Assert.Equal("overloaded", exception.Message);
        Ran(_ => true, Times.Exactly(4));
    }

    [Fact]
    public async Task Retry_OfOneAttempt_NeverTriesAgain()
    {
        FailThenAnswer(100, new PromptSessionException("x"));

        await Assert.ThrowsAsync<PromptSessionException>(() => Limited("go", new PromptRequestLimits { Retry = new PromptRetry { MaxAttempts = 1 } }));

        Ran(_ => true, Times.Once());
    }

    [Fact]
    public async Task Retry_AfterSomethingWasReported_ThrowsInsteadOfRepeatingIt()
    {
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => MessageThenFailure(new PromptSessionException("dropped")));
        List<string> seen = [];

        await Assert.ThrowsAsync<PromptSessionException>(async () =>
        {
            await foreach (PromptEvent output in new PromptRequester(executor.Object).Events("go", new PromptRequestOptions(), new PromptRequestLimits { Retry = Quick() }))
            {
                if (output is PromptMessageEvent message)
                {
                    seen.Add(message.Text);
                }
            }
        });

        Assert.Equal(["partial"], seen);
        Ran(_ => true, Times.Once());
    }

    public static TheoryData<Exception> NotRetried
        => new()
        {
            new PromptLimitException(PromptLimit.Timeout, "t"),
            new PromptSessionOutputException("o", null),
            new PromptToolStartException("missing"),
            new ArgumentException("bad"),
            new InvalidOperationException("bug")
        };

    [Theory]
    [MemberData(nameof(NotRetried))]
    public async Task Retry_IsNotForErrorsThatRetryingCannotFix(Exception failure)
    {
        FailThenAnswer(100, failure);

        await Assert.ThrowsAsync(failure.GetType(), () => Limited("go", new PromptRequestLimits { Retry = Quick() }));

        Ran(_ => true, Times.Once());
    }

    [Fact]
    public async Task Retry_WithAPredicate_DecidesFromTheError()
    {
        FailThenAnswer(1, new InvalidOperationException("transient"), "fine");
        Assert.Equal("fine", await Limited("go", new PromptRequestLimits { Retry = Quick(should: error => error is InvalidOperationException) }));

        FailThenAnswer(100, new PromptSessionException("permanent"));
        await Assert.ThrowsAsync<PromptSessionException>(() => Limited("go", new PromptRequestLimits { Retry = Quick(should: _ => false) }));
    }

    [Fact]
    public async Task Retry_WaitsLongerEachTimeUpToTheLongestWait()
    {
        FailThenAnswer(3, new PromptSessionException("x"), "fine");
        PromptRetry retry = new() { MaxAttempts = 4, InitialDelay = TimeSpan.FromMilliseconds(40), BackoffFactor = 2, MaxDelay = TimeSpan.FromMilliseconds(60) };
        DateTimeOffset start = DateTimeOffset.UtcNow;

        await Limited("go", new PromptRequestLimits { Retry = retry });

        Assert.InRange(DateTimeOffset.UtcNow - start, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Retry_WaitsCountTowardsTheTimeoutAndEndItWithTheTimeoutLimit()
    {
        FailThenAnswer(100, new PromptSessionException("x"));
        PromptRetry retry = new() { MaxAttempts = 5, InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30) };

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => Limited("go", new PromptRequestLimits { Retry = retry, Timeout = TimeSpan.FromMilliseconds(100) }));

        Assert.Equal(PromptLimit.Timeout, exception.Limit);
    }

    [Fact]
    public async Task Retry_WaitIsEndedByTheCallersCancellation()
    {
        FailThenAnswer(100, new PromptSessionException("x"));
        PromptRetry retry = new() { MaxAttempts = 5, InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30) };
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Limited("go", new PromptRequestLimits { Retry = retry }, cancellation.Token));
    }

    private void ToolCalls(int count)
        => executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Events([Message("start"), .. Enumerable.Range(0, count).Select(number => (PromptEvent)new PromptToolCallEvent { Id = number.ToString(), Name = "read_file" }), Message("end")]));

    [Fact]
    public async Task MaxToolCalls_AllowsExactlyThatManyAndStopsTheNext()
    {
        ToolCalls(3);

        Assert.Equal("start\n\nend", await Limited("go", new PromptRequestLimits { MaxToolCalls = 3 }));
        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => Limited("go", new PromptRequestLimits { MaxToolCalls = 2 }));

        Assert.Equal(PromptLimit.ToolCalls, exception.Limit);
    }

    [Fact]
    public async Task MaxToolCalls_ReportsWhatCameBeforeTheCallThatBrokeTheLimit()
    {
        ToolCalls(5);
        List<PromptEvent> seen = [];

        await Assert.ThrowsAsync<PromptLimitException>(async () =>
        {
            await foreach (PromptEvent output in new PromptRequester(executor.Object).Events("go", new PromptRequestOptions(), new PromptRequestLimits { MaxToolCalls = 1 }))
            {
                seen.Add(output);
            }
        });

        Assert.Equal(2, seen.Count);
        Assert.IsType<PromptToolCallEvent>(seen[1]);
    }

    [Fact]
    public async Task MaxToolCalls_OfZero_AllowsNoToolAtAll()
    {
        ToolCalls(1);
        await Assert.ThrowsAsync<PromptLimitException>(() => Limited("go", new PromptRequestLimits { MaxToolCalls = 0 }));

        ToolCalls(0);
        Assert.Equal("start\n\nend", await Limited("go", new PromptRequestLimits { MaxToolCalls = 0 }));
    }

    [Fact]
    public async Task Collect_ReturnsTheTextEveryEventAndTheUsageAddedUp()
    {
        PromptEvent usageOne = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 10, CachedInputTokens = 1, OutputTokens = 2, CostUsd = 0.5m } };
        PromptEvent usageTwo = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 5, OutputTokens = 3 } };
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Events(Message("one"), usageOne, Message("two"), usageTwo));

        PromptResult result = await Session.Result("go");

        Assert.Equal("one\n\ntwo", result.Text);
        Assert.Equal(4, result.Events.Count);
        Assert.Equal(new PromptUsage { InputTokens = 15, CachedInputTokens = 1, OutputTokens = 5, CostUsd = 0.5m }, result.Usage);
    }

    [Fact]
    public async Task Collect_WithASchema_ReturnsTheLastMessageAndWithoutUsageZero()
    {
        Answers("progress", """{"summary":"fine","score":3}""");
        JsonElement schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        PromptResult result = await Session.Result("go", new PromptRequestOptions { Schema = schema });

        Assert.Equal("""{"summary":"fine","score":3}""", result.Text);
        Assert.Equal(new PromptUsage(), result.Usage);
        Assert.Null(result.Usage.CostUsd);
    }

    private PromptTool Tool(string name)
        => new() { Name = name, Description = "d", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement, Handler = (_, _) => Task.FromResult("ok") };

    private async IAsyncEnumerable<PromptEvent> Hang([EnumeratorCancellation] CancellationToken cancellation)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        yield break;
    }
}
