namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptLoopResetTests
{
    private readonly Mock<IPromptExecutor> executor = new();

    private IPromptSession? session;

    private IPromptSession Session => session ??= new ExecutorSession(executor.Object).Session;

    private readonly TimeSpan margin = TimeSpan.FromMilliseconds(20);

    private int sends;

    public PromptLoopResetTests()
        => executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) => Interlocked.Increment(ref sends) == 1 ? Hang(token) : Reply("done"));

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

    private PromptLoop<string> Loop(PromptLoopLimits<string> limits)
        => new(Session, "check", PromptLoopSchedule.Manual, null, text => text, limits, margin);

    private Func<IReadOnlyList<PromptWindow>, bool> Over
        => status => status.Any(window => window.Used >= 1);

    private Func<IReadOnlyList<PromptWindow>, PromptWindow?> Latest
        => windows => windows.Where(window => window.Used >= 1).MaxBy(window => window.ResetsAt);

    private Func<IReadOnlyList<PromptWindow>, PromptWindow?> Soonest
        => windows => windows.MinBy(window => window.ResetsAt);

    private PromptWindow Window(string name, double used, TimeSpan length, DateTimeOffset resets)
        => new() { Name = name, Used = used, Length = length, ReadAt = DateTimeOffset.UtcNow, ResetsAt = resets };

    private async Task Finishes(Task task)
        => await task.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task WhenTheLimitIsReached_TheLoopWaitsForTheResetAndThenSends()
    {
        int reads = 0;
        DateTimeOffset reset = DateTimeOffset.UtcNow.AddMilliseconds(300);
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(() => ++reads == 1 ? TestWindows.Claude(session: 1, sessionResets: reset) : TestWindows.Claude(session: 0.03));
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = Over }, WaitForWindow = Latest, MaxRuns = 1 });
        Recorder<string> recorder = new();
        loop.Replies.Subscribe(recorder);

        await WaitUntil(() => loop.ResumesAt is not null);
        DateTimeOffset? resumes = loop.ResumesAt;
        await Finishes(loop.Task);
        await recorder.Ended();

        Assert.Equal(reset + margin, resumes);
        Assert.Equal(["done"], recorder.Values);
        Assert.Equal(PromptLoopStopReason.MaxRuns, loop.StopReason);
        Assert.Null(loop.ResumesAt);
        Assert.True(DateTimeOffset.UtcNow >= reset);
    }

    [Fact]
    public async Task WaitingForTheWindowTheFunctionChooses_WaitsForItsResetEvenWhenAnotherComesSooner()
    {
        DateTimeOffset session = DateTimeOffset.UtcNow.AddMilliseconds(150);
        DateTimeOffset week = DateTimeOffset.UtcNow.AddMilliseconds(400);
        int reads = 0;
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(() => ++reads == 1 ? TestWindows.Claude(session: 0.4, sessionResets: session, week: 1, weekResets: week) : TestWindows.Claude());
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = Over }, WaitForWindow = Latest, MaxRuns = 1 });

        await WaitUntil(() => loop.ResumesAt is not null);

        Assert.Equal(week + margin, loop.ResumesAt);
        await Finishes(loop.Task);
    }

    [Fact]
    public async Task WhenNoWindowIsUsedUp_TheSoonestResetIsWaitedFor()
    {
        DateTimeOffset soon = DateTimeOffset.UtcNow.AddMilliseconds(200);
        int reads = 0;
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(() => ++reads == 1 ? (IReadOnlyList<PromptWindow>)new PromptWindow[] { new PromptWindow { Name = "5 hours", Used = 0.8, Length = TimeSpan.FromHours(5), ReadAt = DateTimeOffset.UtcNow, ResetsAt = DateTimeOffset.UtcNow.AddHours(3) }, new PromptWindow { Name = "Week", Used = 0.9, Length = TimeSpan.FromDays(7), ReadAt = DateTimeOffset.UtcNow, ResetsAt = soon } } : []);
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = _ => reads == 1 }, WaitForWindow = Soonest, MaxRuns = 1 });

        await WaitUntil(() => loop.ResumesAt is not null);

        Assert.Equal(soon + margin, loop.ResumesAt);
        await Finishes(loop.Task);
    }

    [Fact]
    public async Task WhenTheProviderReportsNoResetTime_TheLoopStopsWithTheWindowLimitAsUsual()
    {
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 1));
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = Over }, WaitForWindow = Latest });

        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.WindowLimit, loop.StopReason);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task WhenTheResetHasPassedAlready_TheLoopStopsWithTheWindowLimitInsteadOfSpinning()
    {
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 1, sessionResets: DateTimeOffset.UtcNow.AddMinutes(-1)));
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = Over }, WaitForWindow = Latest });

        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.WindowLimit, loop.StopReason);
    }

    [Fact]
    public async Task TheTimeoutOfTheLoop_EndsTheWait()
    {
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 1, sessionResets: DateTimeOffset.UtcNow.AddHours(1)));
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = Over }, WaitForWindow = Latest, Timeout = TimeSpan.FromMilliseconds(200) });

        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.Timeout, loop.StopReason);
        Assert.Null(loop.ResumesAt);
    }

    [Fact]
    public async Task WaitingForTheWindowOfTheLimit_IgnoresAnotherWindowThatIsUsedUp()
    {
        DateTimeOffset session = DateTimeOffset.UtcNow.AddMilliseconds(150);
        DateTimeOffset opus = DateTimeOffset.UtcNow.AddHours(5);
        int reads = 0;
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(() => ++reads == 1 ? (IReadOnlyList<PromptWindow>)[Window("Session", 0.95, TimeSpan.FromHours(5), session), Window("Week (Opus)", 1, TimeSpan.FromDays(7), opus)] : [Window("Session", 0.1, TimeSpan.FromHours(5), session.AddHours(5)), Window("Week (Opus)", 1, TimeSpan.FromDays(7), opus)]);
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = windows => windows.Any(window => window.Name == "Session" && window.Used > 0.9) }, WaitForWindow = windows => windows.Single(window => window.Name == "Session"), MaxRuns = 1 });

        await WaitUntil(() => loop.ResumesAt is not null);

        Assert.Equal(session + margin, loop.ResumesAt);
        await Finishes(loop.Task);
        Assert.Equal(PromptLoopStopReason.MaxRuns, loop.StopReason);
    }

    [Fact]
    public async Task WhenTheFunctionChoosesNoWindow_TheLoopStopsWithTheWindowLimitAsUsual()
    {
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 1, sessionResets: DateTimeOffset.UtcNow.AddHours(1)));
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = Over }, WaitForWindow = _ => null });

        await Finishes(loop.Task);

        Assert.Equal(PromptLoopStopReason.WindowLimit, loop.StopReason);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task TheFunctionIsGivenTheWindowsThatReachedTheLimit()
    {
        IReadOnlyList<PromptWindow>? given = null;
        executor.Setup(mock => mock.GetWindows(It.IsAny<CancellationToken>())).ReturnsAsync(TestWindows.Claude(session: 1, week: 0.2));
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { WindowLimit = new PromptWindowLimit { Reached = Over }, WaitForWindow = windows => { given = windows; return null; } });

        await Finishes(loop.Task);

        Assert.Equal(["Session", "Week"], given!.Select(window => window.Name));
    }

    [Fact]
    public void WaitingForAWindow_NeedsAWindowLimitToSayWhenToWait()
        => Assert.Throws<ArgumentException>(() => Loop(new PromptLoopLimits<string> { WaitForWindow = Latest }));

    [Fact]
    public void ResumesAt_IsNullWhileTheLoopIsNotWaiting()
    {
        using PromptLoop<string> loop = Loop(new PromptLoopLimits<string> { MaxRuns = 1 });

        Assert.Null(loop.ResumesAt);
    }

    private async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
