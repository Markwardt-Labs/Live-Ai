namespace Markwardt.LiveAi.Tests.Unit;

[Collection("Processes")]
public sealed class PromptTasksTests : IAsyncLifetime
{
    private readonly ManualTime time = new();

    private readonly PromptTasks tasks;

    private readonly TempFolder temp = new();

    public PromptTasksTests()
        => tasks = new PromptTasks(time);

    public Task InitializeAsync()
        => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await tasks.DisposeAsync();
        temp.Dispose();
    }

    private string Forever()
        => tasks.StartHelper("endless", async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        });

    private async Task Pause()
        => await Task.Delay(150);

    private async Task Timers(int count)
    {
        for (int attempt = 0; attempt < 500 && time.CreatedTimers < count; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(time.CreatedTimers >= count);
    }

    [Fact]
    public async Task Wait_EndsWhenItsTimeIsUpOnTheClockOfTheTasksAndNotBefore()
    {
        Forever();

        Task<string> waiting = tasks.Wait(null, 60, CancellationToken.None);
        await Pause();
        time.Advance(TimeSpan.FromSeconds(59));
        await Pause();
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        string report = await waiting;

        Assert.StartsWith("task-1 is still running after 1 min 0 s", report, StringComparison.Ordinal);
        Assert.Contains("The time was up and no task has ended", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("first line\r\nsecond line")]
    [InlineData("a\r\n\r\nb")]
    [InlineData("a task with a description that goes on past eighty characters so that the report has to cut it short somewhere")]
    public async Task Wait_ReportsARunningTaskWhateverLineEndingsItsDescriptionHas(string description)
    {
        tasks.StartHelper(description, async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        });

        string report = await tasks.Wait(null, 0, CancellationToken.None);

        Assert.StartsWith("task-1 is still running after 1 s (", report, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', report);
    }

    [Fact]
    public async Task Wait_WithZeroSeconds_OnlyReportsHowLongATaskHasRunByTheClock()
    {
        Forever();
        time.Advance(TimeSpan.FromSeconds(125));

        string report = await tasks.Wait(null, 0, CancellationToken.None);

        Assert.StartsWith("task-1 is still running after 2 min 5 s", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndedTask_IsReportedWithTheTimeItTookByTheClock()
    {
        TaskCompletionSource<string> release = new();
        tasks.StartHelper("slow answer", _ => release.Task);
        time.Advance(TimeSpan.FromSeconds(7));
        release.SetResult("the answer");

        string report = await tasks.Wait(null, 60, CancellationToken.None);

        Assert.StartsWith("task-1 finished after 7 s.\nthe answer", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_NeverMissesATaskThatEndsAsItStartsWaiting()
    {
        int stuck = 0;
        List<Task> workers = [];

        for (int worker = 0; worker < 8; worker++)
        {
            workers.Add(Task.Run(async () =>
            {
                for (int round = 0; round < 60; round++)
                {
                    ManualTime clock = new();
                    PromptTasks own = new(clock);
                    TaskCompletionSource<string> release = new();
                    own.StartHelper("answer", _ => release.Task);
                    release.SetResult("done");
                    Task<string> waiting = own.Wait(null, 60, CancellationToken.None);

                    if (await Task.WhenAny(waiting, Task.Delay(5000)) != waiting)
                    {
                        Interlocked.Increment(ref stuck);
                        return;
                    }

                    await own.DisposeAsync();
                }
            }));
        }

        await Task.WhenAll(workers);

        Assert.Equal(0, stuck);
    }

    private async Task Finished(int count)
    {
        for (int index = 1; index <= count; index++)
        {
            tasks.StartHelper($"helper {index}", _ => Task.FromResult($"result {index}"));
            await tasks.Wait([$"task-{index}"], 60, CancellationToken.None);
        }
    }

    [Fact]
    public async Task EndedTasksTheAiWasToldAboutAreClearedBeyondTheLatestTwentyAndSaySoWhenAskedFor()
    {
        await Finished(25);

        InvalidOperationException cleared = await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.Wait(["task-5"], 0, CancellationToken.None));
        InvalidOperationException output = Assert.Throws<InvalidOperationException>(() => tasks.Output("task-1", 5));
        InvalidOperationException stop = Assert.Throws<InvalidOperationException>(() => tasks.Stop(" task-2 "));

        Assert.Equal("task-5 ended and was cleared: only the 20 latest ended tasks are kept once they have been reported.", cleared.Message);
        Assert.StartsWith("task-1 ended and was cleared", output.Message, StringComparison.Ordinal);
        Assert.StartsWith("task-2 ended and was cleared", stop.Message, StringComparison.Ordinal);
        Assert.Equal("Nothing new: every task you asked about was already reported.", await tasks.Wait(["task-6"], 0, CancellationToken.None));
        Assert.Equal("Nothing new: every task you asked about was already reported.", await tasks.Wait(["task-25"], 0, CancellationToken.None));
    }

    [Fact]
    public async Task ATaskThatWasNeverStartedIsStillUnknownAndNotCleared()
    {
        await Finished(3);

        InvalidOperationException unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.Wait(["task-9"], 0, CancellationToken.None));
        InvalidOperationException odd = Assert.Throws<InvalidOperationException>(() => tasks.Output("task-x", 5));

        Assert.Equal("There is no task task-9.", unknown.Message);
        Assert.Equal("There is no task task-x.", odd.Message);
    }

    [Fact]
    public async Task EndedTasksTheAiWasNotToldAboutAreNeverCleared()
    {
        for (int batch = 0; batch < 2; batch++)
        {
            for (int index = 1; index <= 13; index++)
            {
                tasks.StartHelper($"helper {batch * 13 + index}", _ => Task.FromResult($"result {batch * 13 + index}"));
            }

            await Task.Delay(300);
        }

        tasks.StartHelper("one more", _ => Task.FromResult("last"));
        string first = await tasks.Wait(["task-1"], 60, CancellationToken.None);

        Assert.StartsWith("task-1 finished after", first, StringComparison.Ordinal);
        Assert.Contains("result 1", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunningTaskIsNeverClearedHoweverManyOthersEnd()
    {
        Forever();
        for (int index = 2; index <= 26; index++)
        {
            tasks.StartHelper($"helper {index}", _ => Task.FromResult("quick"));
            await tasks.Wait([$"task-{index}"], 60, CancellationToken.None);
        }

        string report = await tasks.Wait(["task-1"], 0, CancellationToken.None);

        Assert.StartsWith("task-1 is still running", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_CancelledByTheCaller_Throws()
    {
        Forever();
        using CancellationTokenSource cancel = new();

        Task<string> waiting = tasks.Wait(null, 60, cancel.Token);
        await Pause();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Wait_GivesAnotherTaskThatEndsAtTheSameTimeTheGraceOfTheClockBeforeItReports()
    {
        TaskCompletionSource<string> first = new();
        TaskCompletionSource<string> second = new();
        tasks.StartHelper("first", _ => first.Task);
        tasks.StartHelper("second", _ => second.Task);
        Task<string> waiting = tasks.Wait(null, 60, CancellationToken.None);
        await Pause();

        first.SetResult("one");
        await Timers(2);
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(300));
        string report = await waiting;

        Assert.Contains("task-1 finished", report, StringComparison.Ordinal);
        Assert.Contains("task-2 is still running", report, StringComparison.Ordinal);
        second.SetResult("two");
    }

    [Fact]
    public async Task ACommand_IsStoppedWhenItsTimeLimitPassesOnTheClock()
    {
        tasks.StartCommand(temp.Path, "sleep 60", 10);
        await Pause();

        time.Advance(TimeSpan.FromSeconds(10));
        string report = await tasks.Wait(["task-1"], 60, CancellationToken.None);

        Assert.Contains("task-1 timed out and was stopped (limit 10 seconds) after 10 s.", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommand_ThatEndsOnItsOwn_IsNotCutOffByTheClockAndKeepsItsExitCode()
    {
        tasks.StartCommand(temp.Path, "exit 3", 10);

        string report = await tasks.Wait(["task-1"], 60, CancellationToken.None);

        Assert.Contains("task-1 ended with exit code 3 after 1 s.", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_GivesUpOnATaskThatIgnoresBeingStoppedWhenFiveSecondsPassOnTheClock()
    {
        PromptTasks stubborn = new(time);
        TaskCompletionSource<string> never = new();
        stubborn.StartHelper("deaf", _ => never.Task);

        ValueTask disposing = stubborn.DisposeAsync();
        await Pause();
        Assert.False(disposing.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));
        await disposing;

        never.SetResult("late");
    }

    [Fact]
    public async Task DisposeAsync_ReturnsAtOnceWhenTheTasksEndWhenStopped()
    {
        PromptTasks own = new(time);
        own.StartHelper("endless", async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        });

        await own.DisposeAsync();
    }
}
