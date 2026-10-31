namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptLoopLimitsTests
{
    private readonly Mock<IPromptExecutor> executor = new();

    private IPromptSession? session;

    private IPromptSession Session => session ??= new ExecutorSession(executor.Object).Session;

    private int sends;

    private Func<int, CancellationToken, IAsyncEnumerable<PromptEvent>> behavior;

    public PromptLoopLimitsTests()
    {
        behavior = (_, _) => Reply("done");
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Held(behavior(Interlocked.Increment(ref sends), token)));
    }

    private TaskCompletionSource? hold;

    private void Hold()
        => hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async IAsyncEnumerable<PromptEvent> Held(IAsyncEnumerable<PromptEvent> events)
    {
        if (hold is { } gate)
        {
            await gate.Task;
        }

        await foreach (PromptEvent output in events)
        {
            yield return output;
        }
    }

    private async IAsyncEnumerable<PromptEvent> Reply(string text)
    {
        await Task.Yield();
        yield return new PromptMessageEvent { Text = text };
    }

    private async IAsyncEnumerable<PromptEvent> Hang([EnumeratorCancellation] CancellationToken cancellation)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        yield break;
    }

    private async Task Finishes(Task task)
        => await task.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task MaxRuns_StopsAfterThatManySendsAndStillPublishesTheLastReply()
    {
        Hold();
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { MaxRuns = 3 });
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);
        hold!.SetResult();

        await Finishes(loop.Task);
        await recorder.Ended();

        Assert.Equal(3, sends);
        Assert.Equal(["done", "done", "done"], recorder.Values);
        Assert.True(recorder.IsCompleted);
        Assert.Equal(PromptLoopStopReason.MaxRuns, loop.StopReason);
    }

    [Fact]
    public async Task Until_StopsAfterTheReplyItAcceptsHasBeenPublished()
    {
        behavior = (number, _) => Reply(number == 3 ? "stop" : "go");
        Hold();
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { Until = reply => reply == "stop" });
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);
        hold!.SetResult();

        await Finishes(loop.Task);
        await recorder.Ended();

        Assert.Equal(["go", "go", "stop"], recorder.Values);
        Assert.Equal(PromptLoopStopReason.Until, loop.StopReason);
    }

    [Fact]
    public async Task Until_ThatThrows_FailsTheLoop()
    {
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { Until = _ => throw new InvalidOperationException("bad") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.Task);

        Assert.Equal(PromptLoopStopReason.Failed, loop.StopReason);
    }

    [Fact]
    public async Task Timeout_CancelsASendThatIsStillRunningAndEndsTheLoopWithoutAnError()
    {
        behavior = (_, token) => Hang(token);
        Hold();
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { Timeout = TimeSpan.FromMilliseconds(100) });
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);
        hold!.SetResult();

        await Finishes(loop.Task);
        await recorder.Ended();

        Assert.Equal(PromptLoopStopReason.Timeout, loop.StopReason);
        Assert.True(recorder.IsCompleted);
        Assert.Null(recorder.Error);
        Assert.Empty(recorder.Values);
    }

    private async IAsyncEnumerable<PromptEvent> Used(long tokens, decimal? cost = null)
    {
        await Task.Yield();
        yield return new PromptMessageEvent { Text = "done" };
        yield return new PromptUsageEvent { Usage = new PromptUsage { InputTokens = tokens, CostUsd = cost } };
    }

    [Fact]
    public async Task Budget_OfTokens_StopsTheLoopAfterTheSendThatReachesItAndPublishesThatReply()
    {
        behavior = (_, _) => Used(10);
        Hold();
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { Budget = new PromptBudget { MaxTokens = 25 } });
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);
        hold!.SetResult();

        await Finishes(loop.Task);
        await recorder.Ended();

        Assert.Equal(3, sends);
        Assert.Equal(3, recorder.Values.Count);
        Assert.Equal(PromptLoopStopReason.Budget, loop.StopReason);
    }

    [Fact]
    public async Task Budget_OfCost_CountsTheCostOfEverySend()
    {
        behavior = (_, _) => Used(1, 0.4m);
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { Budget = new PromptBudget { MaxCostUsd = 1m } });

        await Finishes(loop.Task);

        Assert.Equal(3, sends);
        Assert.Equal(PromptLoopStopReason.Budget, loop.StopReason);
    }

    [Fact]
    public async Task Budget_YieldsToTheOtherLimitsThatComeFirst()
    {
        behavior = (_, _) => Used(10);
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { MaxRuns = 2, Budget = new PromptBudget { MaxTokens = 1000 } });

        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.MaxRuns, loop.StopReason);
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Loop("check", limits: new PromptLoopLimits<string> { Budget = new PromptBudget { MaxTokens = 0 } }));
    }

    [Fact]
    public async Task Timeout_UpToTheLongestATimerCanCount_IsAcceptedAndLongerIsRefused()
    {
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { Timeout = TimeSpan.FromDays(24), MaxRuns = 1 });

        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.MaxRuns, loop.StopReason);
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Loop("check", limits: new PromptLoopLimits<string> { Timeout = TimeSpan.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Loop("check", limits: new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = _ => false, Interval = TimeSpan.FromDays(30) } }));
    }

    [Fact]
    public async Task Timeout_AlsoEndsTheWaitBetweenSends()
    {
        using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual, limits: new PromptLoopLimits<string> { Timeout = TimeSpan.FromMilliseconds(150) });

        await Finishes(loop.Task);

        Assert.Equal(1, sends);
        Assert.Equal(PromptLoopStopReason.Timeout, loop.StopReason);
        loop.Trigger();
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task WindowLimit_StopsTheLoopWithoutAnErrorWhenItIsReached()
    {
        behavior = (_, token) => Hang(token);
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(week: 0.99));
        Hold();
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = status => status.Used("Week", 0.95) } });
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);
        hold!.SetResult();

        await Finishes(loop.Task);
        await recorder.Ended();

        Assert.Equal(PromptLoopStopReason.WindowLimit, loop.StopReason);
        Assert.True(recorder.IsCompleted);
        Assert.Null(recorder.Error);
    }

    [Fact]
    public async Task WindowLimit_ThatIsNotReached_LetsTheLoopRunOn()
    {
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(week: 0.1));
        using IPromptLoop<string> loop = Session.Loop("check", limits: new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = _ => false, Interval = TimeSpan.FromMilliseconds(20) }, MaxRuns = 4 });

        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.MaxRuns, loop.StopReason);
        Assert.Equal(4, sends);
    }

    [Fact]
    public async Task ATimeoutOfTheSession_AppliesToEachSendAndFailsTheLoop()
    {
        behavior = (_, token) => Hang(token);
        using IPromptLoop<string> loop = new ExecutorSession(executor.Object, new PromptRequestLimits { Timeout = TimeSpan.FromMilliseconds(50) }).Session.Loop("check");

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => loop.Task);

        Assert.Equal(PromptLimit.Timeout, exception.Limit);
        Assert.Equal(PromptLoopStopReason.Failed, loop.StopReason);
    }

    [Fact]
    public async Task StopReason_IsRunningUntilTheLoopStopsAndThenDisposedWhenItWasDisposed()
    {
        behavior = (_, token) => Hang(token);
        IPromptLoop<string> loop = Session.Loop("check");

        Assert.Equal(PromptLoopStopReason.Running, loop.StopReason);
        loop.Dispose();
        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.Disposed, loop.StopReason);
    }

    [Fact]
    public async Task StopReason_IsDisposedAfterAGracefulDisposalToo()
    {
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);

        await loop.DisposeAsync();

        Assert.Equal(PromptLoopStopReason.Disposed, loop.StopReason);
    }

    [Fact]
    public async Task StopReason_IsFailedWhenASendFails()
    {
        behavior = (_, _) => throw new PromptSessionException("broken");
        using IPromptLoop<string> loop = Session.Loop("check");

        await Assert.ThrowsAsync<PromptSessionException>(() => loop.Task);

        Assert.Equal(PromptLoopStopReason.Failed, loop.StopReason);
    }

    [Fact]
    public void Limits_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Loop("check", limits: new PromptLoopLimits<string> { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Loop("check", limits: new PromptLoopLimits<string> { MaxRuns = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Loop("check", limits: new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = _ => false, Interval = TimeSpan.Zero } }));
    }

    [Fact]
    public async Task ATypedLoop_PublishesTheAnswersAsTheTypeAndStopsOnTheTypedCondition()
    {
        behavior = (number, _) => Reply($$"""{"summary":"s{{number}}","score":{{number}}}""");
        Hold();
        using IPromptLoop<Answer> loop = Session.Loop<Answer>("check", PromptLoopSchedule.Every(TimeSpan.Zero), limits: new PromptLoopLimits<Answer> { Until = answer => answer.Score >= 3 });
        Recorder<Answer> recorder = new();
        loop.Replies.Subscribe(recorder);
        hold!.SetResult();

        await Finishes(loop.Task);
        await recorder.Ended();

        Assert.Equal([new Answer("s1", 1), new Answer("s2", 2), new Answer("s3", 3)], recorder.Values);
        Assert.Equal(PromptLoopStopReason.Until, loop.StopReason);
        executor.Verify(mock => mock.Execute(It.Is<PromptRun>(run => run.Schema.HasValue), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ATypedLoop_WithAnAnswerThatIsNotTheType_FailsWithTheOutputError()
    {
        behavior = (_, _) => Reply("""{"unexpected":true}""");
        Hold();
        using IPromptLoop<Answer> loop = Session.Loop<Answer>("check");
        Recorder<Answer> recorder = new();
        loop.Replies.Subscribe(recorder);
        hold!.SetResult();

        await Assert.ThrowsAsync<PromptSessionOutputException>(() => loop.Task);
        await recorder.Ended();

        Assert.IsType<PromptSessionOutputException>(recorder.Error);
        Assert.Equal(PromptLoopStopReason.Failed, loop.StopReason);
    }

    [Fact]
    public async Task ATypedLoop_WithoutAnyAnswer_FailsWithTheOutputError()
    {
        behavior = (_, _) => Nothing();
        using IPromptLoop<Answer> loop = Session.Loop<Answer>("check");

        await Assert.ThrowsAsync<PromptSessionOutputException>(() => loop.Task);
    }

    private async IAsyncEnumerable<PromptEvent> Nothing()
    {
        await Task.Yield();
        yield break;
    }
}
