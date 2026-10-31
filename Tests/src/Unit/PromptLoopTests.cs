namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptLoopTests
{
    private readonly Mock<IPromptExecutor> executor = new();

    private IPromptSession? session;

    private IPromptSession Session => session ??= new ExecutorSession(executor.Object).Session;

    private readonly List<string> sent = [];

    private int active;

    private int mostActive;

    private Func<CancellationToken, Task> work = _ => Task.CompletedTask;

    public PromptLoopTests()
        => executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun run, CancellationToken token) => Run(run.Text, token));

    private bool Idle
    {
        get
        {
            lock (sent)
            {
                return active == 0;
            }
        }
    }

    private int Sends
    {
        get
        {
            lock (sent)
            {
                return sent.Count;
            }
        }
    }

    private async IAsyncEnumerable<PromptEvent> Run(string prompt, [EnumeratorCancellation] CancellationToken cancellation)
    {
        lock (sent)
        {
            sent.Add(prompt);
            active++;
            mostActive = Math.Max(mostActive, active);
        }

        try
        {
            await work(cancellation);
            yield return new PromptMessageEvent { Text = "done" };
        }
        finally
        {
            lock (sent)
            {
                active--;
            }
        }
    }

    private async Task Eventually(Func<bool> check)
    {
        for (int attempt = 0; attempt < 500 && !check(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(check());
    }

    [Fact]
    public async Task Loop_SendsThePromptOnceAtOnceAndThenWaitsForATriggerWhenThereIsNoInterval()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);

        await Eventually(() => Sends == 1);
        await Task.Delay(150);

        Assert.Equal(["check"], sent);
        Assert.Equal(PromptLoopSchedule.Manual, loop.Schedule);
        Assert.Equal("check", loop.Input);
        Assert.False(loop.Task.IsCompleted);
    }

    [Fact]
    public async Task Loop_WithAnInterval_StartsWithItAndSendsAgainAndAgain()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(30)));

        Assert.Equal(PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(30)), loop.Schedule);
        await Eventually(() => Sends >= 4);
    }

    [Fact]
    public async Task Loop_WithAnInputObject_SendsItAsJsonAndTakesAnInterval()
    {
        await using IPromptLoop<string> loop = Session.Loop(new Question("rate", ["a"], Level.Low), PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(30)));

        await Eventually(() => Sends >= 3);
        Assert.All(sent, prompt => Assert.Equal("""{"task":"rate","items":["a"],"level":"Low"}""", prompt));
        Assert.Equal(PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(30)), loop.Schedule);
    }

    [Fact]
    public async Task Loop_WithAnInputObjectAndNoInterval_SendsOnceAndThenWaitsForATrigger()
    {
        await using IPromptLoop<string> loop = Session.Loop(new Question("rate", [], Level.High), PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);
        await Task.Delay(100);

        Assert.Equal(PromptLoopSchedule.Manual, loop.Schedule);
        Assert.Equal(1, Sends);
        loop.Trigger();
        await Eventually(() => Sends == 2);
    }

    [Fact]
    public async Task Replies_PublishesTheAnswerOfEachSendInOrder()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        Recorder<string> recorder = new();
        using IDisposable subscription = loop.Replies.Subscribe(recorder);
        await Eventually(() => recorder.Values.Count == 1);

        loop.Trigger();
        await Eventually(() => recorder.Values.Count == 2);

        Assert.Equal(["done", "done"], recorder.Values);
        Assert.False(recorder.IsCompleted);
    }

    [Fact]
    public async Task Replies_JoinsTheMessagesOfASendLikeAsk()
    {
        executor.Setup(mock => mock.Execute(It.Is<PromptRun>(run => run.Text == "multi"), It.IsAny<CancellationToken>())).Returns(() => Two());
        await using IPromptLoop<string> loop = Session.Loop("multi", PromptLoopSchedule.Manual);
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);

        await Eventually(() => recorder.Values.Count == 1);

        Assert.Equal(["first\n\nsecond"], recorder.Values);
    }

    [Fact]
    public async Task AnIntervalSchedule_RunsOnTheClockOfTheSession()
    {
        ManualTime clock = new();
        await using IPromptLoop<string> loop = new ExecutorSession(executor.Object, null, clock).Session.Loop("check", PromptLoopSchedule.Every(TimeSpan.FromHours(1)));
        await Eventually(() => Sends == 1);
        await Task.Delay(100);

        clock.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(100);
        Assert.Equal(1, Sends);
        clock.Advance(TimeSpan.FromMinutes(31));
        await Eventually(() => Sends == 2);

        Assert.Equal(2, Sends);
    }

    [Fact]
    public async Task Replies_AWatcherThatArrivesLateIsGivenTheLatestAnswerAndCanStop()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);
        await Task.Delay(50);
        Recorder<string> recorder = new();

        IDisposable subscription = loop.Replies.Subscribe(recorder);
        await recorder.Until(values => values.Count == 1);
        Assert.Equal(["done"], recorder.Values);
        loop.Trigger();
        await Eventually(() => recorder.Values.Count == 2);
        subscription.Dispose();
        loop.Trigger();
        await Eventually(() => Sends == 3);
        await Task.Delay(50);

        Assert.Equal(2, recorder.Values.Count);
    }

    [Fact]
    public async Task Replies_CompletesWhenTheLoopIsDisposedAndTellsLateWatchersAtOnce()
    {
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);
        await Eventually(() => recorder.Values.Count == 1);

        await loop.DisposeAsync();
        Recorder<string> late = new();
        loop.Replies.Subscribe(late);
        await recorder.Ended();
        await late.Ended();

        Assert.True(recorder.IsCompleted);
        Assert.True(late.IsCompleted);
        Assert.Null(recorder.Error);
    }

    [Fact]
    public async Task Replies_FailsWithTheSameErrorTheTaskFaultsWith()
    {
        work = _ => throw new PromptSessionException("it broke");
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => loop.Task);
        await recorder.Ended();

        Assert.Same(exception, recorder.Error);
        Assert.Empty(recorder.Values);
        Recorder<string> late = new();
        loop.Replies.Subscribe(late);
        await late.Ended();
        Assert.Same(exception, late.Error);
        await loop.DisposeAsync();
    }

    [Fact]
    public async Task Replies_AWatcherThatThrows_DoesNotStopTheLoopOrTheOtherWatchers()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(20)));
        Mock<IObserver<string>> bad = new();
        bad.Setup(mock => mock.OnNext(It.IsAny<string>())).Throws(new InvalidOperationException("bad watcher"));
        Recorder<string> good = new();
        loop.Replies.Subscribe(bad.Object);
        loop.Replies.Subscribe(good);

        await Eventually(() => good.Values.Count >= 3);

        Assert.False(loop.Task.IsCompleted);
    }

    private async IAsyncEnumerable<PromptEvent> Two()
    {
        await Task.Yield();
        yield return new PromptMessageEvent { Text = "first" };
        yield return new PromptMessageEvent { Text = "second" };
    }

    [Fact]
    public async Task Trigger_SendsAgainAtOnce()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);
        await Eventually(() => recorder.Values.Count == 1);

        loop.Trigger();
        await Eventually(() => recorder.Values.Count == 2);
        loop.Trigger();
        await Eventually(() => recorder.Values.Count == 3);
        await Task.Delay(100);

        Assert.Equal(3, Sends);
    }

    [Fact]
    public async Task Trigger_WhileASendIsInProgress_DoesNothing()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        work = _ => release.Task;
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);

        loop.Trigger();
        loop.Trigger();
        release.SetResult();
        await Task.Delay(150);

        Assert.Equal(1, Sends);
    }

    [Fact]
    public async Task Trigger_RightAfterTheLoopWasMade_DoesNotSendTwice()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        work = _ => release.Task;
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);

        loop.Trigger();
        release.SetResult();
        await Task.Delay(150);

        Assert.Equal(1, Sends);
    }

    [Fact]
    public async Task Interval_SendsAgainAndAgain()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(30));

        await Eventually(() => Sends >= 4);
    }

    [Fact]
    public async Task Interval_IsMeasuredFromTheStartOfASendAndNeverOverlapsSends()
    {
        work = cancellation => Task.Delay(120, cancellation);
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(20));
        DateTime begun = DateTime.UtcNow;

        await Eventually(() => Sends >= 3);

        Assert.Equal(1, mostActive);
        Assert.True(DateTime.UtcNow - begun >= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Interval_OfAnyLength_DoesNotFaultTheLoopAndIsStillChangeable()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Every(TimeSpan.MaxValue));
        await Eventually(() => Sends == 1);
        await Task.Delay(100);

        Assert.False(loop.Task.IsCompleted);
        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(10));
        await Eventually(() => Sends >= 2);
    }

    [Fact]
    public async Task Interval_LongerThanTheSend_WaitsForTheRestOfIt()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);

        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(400));
        await Task.Delay(150);
        Assert.Equal(1, Sends);

        await Eventually(() => Sends == 2);
    }

    [Fact]
    public async Task Interval_ChangedWhileWaiting_AppliesAtOnce()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMinutes(10));
        await Eventually(() => Sends == 1);
        await Task.Delay(100);

        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(10));
        await Eventually(() => Sends >= 2);

        loop.Schedule = PromptLoopSchedule.Manual;
        await Task.Delay(100);
        int settled = Sends;
        await Task.Delay(150);
        Assert.Equal(settled, Sends);
    }

    [Fact]
    public async Task Trigger_DuringTheWaitForTheInterval_SendsAtOnceAndRestartsTheInterval()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMinutes(10));
        await Eventually(() => Sends == 1 && Idle);

        loop.Trigger();
        await Eventually(() => Sends == 2);
        await Task.Delay(100);

        Assert.Equal(2, Sends);
    }

    [Fact]
    public async Task Prompt_ChangeAppliesToTheNextSendOnly()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        work = _ => release.Task;
        await using IPromptLoop<string> loop = Session.Loop("first", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);

        loop.Input = "second";
        release.SetResult();
        await Task.Delay(50);
        loop.Trigger();
        await Eventually(() => Sends == 2);

        Assert.Equal(["first", "second"], sent);
    }

    [Fact]
    public async Task OptionsAndInput_CanBeReadBackAndRefuseNull()
    {
        await using IPromptLoop<string> loop = Session.Loop("first", PromptLoopSchedule.Manual);
        PromptRequestOptions options = new() { Notes = ["n"] };

        loop.Options = options;

        Assert.Same(options, loop.Options);
        Assert.Equal("first", loop.Input);
        Assert.Throws<ArgumentNullException>(() => loop.Options = null!);
        Assert.Throws<ArgumentNullException>(() => loop.Input = null!);
    }

    [Fact]
    public async Task Dispose_StopsTheLoopAtOnceAndCancelsTheSendInProgress()
    {
        CancellationToken seen = default;
        work = cancellation =>
        {
            seen = cancellation;
            return Task.Delay(Timeout.Infinite, cancellation);
        };
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Continuous);
        await Eventually(() => Sends == 1);

        loop.Dispose();
        loop.Dispose();
        await loop.DisposeAsync();
        loop.Trigger();

        Assert.True(seen.IsCancellationRequested);
        Assert.True(loop.Task.IsCompletedSuccessfully);
        await Task.Delay(100);
        Assert.Equal(1, Sends);
    }

    [Fact]
    public async Task DisposeAsync_LetsTheSendInProgressFinishAndStartsNoOther()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        work = async cancellation =>
        {
            seen = cancellation;
            await release.Task;
        };
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Continuous);
        await Eventually(() => Sends == 1);

        ValueTask disposing = loop.DisposeAsync();
        await Task.Delay(150);
        Assert.False(disposing.IsCompleted);
        Assert.False(seen.IsCancellationRequested);
        loop.Trigger();
        release.SetResult();
        await disposing;
        await Task.Delay(100);

        Assert.True(loop.Task.IsCompletedSuccessfully);
        Assert.Equal(1, Sends);
    }

    [Fact]
    public async Task DisposeAsync_WhileWaitingForTheInterval_StopsAtOnce()
    {
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Every(TimeSpan.FromMinutes(10)));
        await Eventually(() => Sends == 1);
        await Task.Delay(50);

        await loop.DisposeAsync();

        Assert.True(loop.Task.IsCompletedSuccessfully);
        Assert.Equal(1, Sends);
    }

    [Fact]
    public async Task Dispose_WhileAGracefulStopWaits_CancelsTheSend()
    {
        work = cancellation => Task.Delay(Timeout.Infinite, cancellation);
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);
        ValueTask disposing = loop.DisposeAsync();
        await Task.Delay(100);
        Assert.False(disposing.IsCompleted);

        loop.Dispose();

        await disposing;
        Assert.True(loop.Task.IsCompleted);
    }

    [Fact]
    public async Task Prompt_SetFromAnObjectWithJson_IsSentAsJsonByTheNextSend()
    {
        await using IPromptLoop<string> loop = Session.Loop("first", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);

        loop.Input = new Question("rate", ["a"], Level.High);
        loop.Trigger();

        await Eventually(() => Sends == 2);
        Assert.Equal("""{"task":"rate","items":["a"],"level":"High"}""", sent[1]);
    }

    [Fact]
    public async Task Dispose_Synchronously_StopsTheLoopToo()
    {
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        loop.Schedule = PromptLoopSchedule.Every(TimeSpan.FromMilliseconds(20));
        await Eventually(() => Sends >= 2);

        loop.Dispose();

        await loop.Task;
        int settled = Sends;
        await Task.Delay(100);
        Assert.Equal(settled, Sends);
    }

    [Fact]
    public async Task ASendThatFails_StopsTheLoopAndFaultsTheTask()
    {
        work = _ => throw new PromptSessionException("it broke");
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        loop.Schedule = PromptLoopSchedule.Continuous;

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => loop.Task);

        Assert.Equal("it broke", exception.Message);
        loop.Trigger();
        await Task.Delay(100);
        Assert.Equal(1, Sends);
    }

    [Fact]
    public async Task DisposeAsync_AfterAFailure_DoesNotThrow()
    {
        work = _ => throw new PromptSessionException("it broke");
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        await Eventually(() => loop.Task.IsFaulted);

        await loop.DisposeAsync();

        Assert.True(loop.Task.IsFaulted);
    }

    [Fact]
    public async Task Continuous_SendsAgainAsSoonAsEachSendIsDoneAndNeverOverlaps()
    {
        work = cancellation => Task.Delay(15, cancellation);
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Continuous);

        await Eventually(() => Sends >= 5);

        Assert.Equal(1, mostActive);
        Assert.Equal(PromptLoopSchedule.Continuous, loop.Schedule);
    }

    [Fact]
    public async Task Continuous_IgnoresTriggersWhileASendIsInProgress()
    {
        work = cancellation => Task.Delay(30, cancellation);
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Continuous);

        for (int index = 0; index < 20; index++)
        {
            loop.Trigger();
            await Task.Delay(5);
        }

        Assert.Equal(1, mostActive);
    }

    [Fact]
    public async Task Manual_NeverSendsAgainOnItsOwnEvenWhenSendsAreQuick()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);

        await Task.Delay(200);

        Assert.Equal(1, Sends);
    }

    [Fact]
    public async Task Schedule_ChangedFromManualToContinuousAndBack_StartsAndStopsTheRepeating()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Manual);
        await Eventually(() => Sends == 1);
        await Task.Delay(50);
        Assert.Equal(1, Sends);

        loop.Schedule = PromptLoopSchedule.Continuous;
        await Eventually(() => Sends >= 4);

        loop.Schedule = PromptLoopSchedule.Manual;
        await Task.Delay(100);
        int settled = Sends;
        await Task.Delay(150);
        Assert.Equal(settled, Sends);
    }

    [Fact]
    public async Task Schedule_ChangedFromAnIntervalToContinuous_AppliesToAWaitThatIsGoingOn()
    {
        await using IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Every(TimeSpan.FromMinutes(10)));
        await Eventually(() => Sends == 1);
        await Task.Delay(50);

        loop.Schedule = PromptLoopSchedule.Continuous;

        await Eventually(() => Sends >= 3);
    }

    [Fact]
    public async Task Loop_WithoutArguments_IsContinuousAndWithAnIntervalIsEvery()
    {
        await using IPromptLoop<string> continuous = Session.Loop("a");
        await using IPromptLoop<string> timed = Session.Loop("b", PromptLoopSchedule.Every(TimeSpan.FromHours(1)));

        Assert.Equal(PromptLoopSchedule.Continuous, continuous.Schedule);
        Assert.Equal(PromptLoopSchedule.Every(TimeSpan.FromHours(1)), timed.Schedule);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Task.Run(() => Session.Loop("c", PromptLoopSchedule.Every(TimeSpan.FromSeconds(-1)))));
    }

    [Fact]
    public async Task Continuous_WithSendsThatFinishAtOnce_ReturnsFromLoopAndStopsWhenDisposed()
    {
        IPromptLoop<string> loop = Session.Loop("check", PromptLoopSchedule.Continuous);
        await Eventually(() => Sends >= 50);

        await loop.DisposeAsync();

        Assert.True(loop.Task.IsCompletedSuccessfully);
        int settled = Sends;
        await Task.Delay(100);
        Assert.Equal(settled, Sends);
    }
}
