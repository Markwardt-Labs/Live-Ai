namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class BackgroundTaskTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly PromptTasks tasks = new();

    private readonly PromptTool shell;

    private readonly PromptTool delegation;

    private readonly Dictionary<string, PromptTool> following;

    public BackgroundTaskTests()
    {
        PromptLibraryTools tools = new();
        shell = tools.CreateShell(temp.Path, tasks);
        delegation = tools.CreateDelegate((task, _, cancellation) => Helper(task, cancellation), tasks);
        following = tools.CreateTasks(tasks).ToDictionary(tool => tool.Name);
    }

    public Task InitializeAsync()
        => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await tasks.DisposeAsync();
        temp.Dispose();
    }

    private async Task<string> Helper(string task, CancellationToken cancellation)
    {
        if (task == "slow")
        {
            await Task.Delay(Timeout.Infinite, cancellation);
        }

        if (task == "fail")
        {
            throw new InvalidOperationException("the helper broke");
        }

        await Task.Delay(100, cancellation);
        return $"helper did {task}";
    }

    private Task<string> Call(PromptTool tool, string json) => tool.Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);

    private static string Escaped(string command) => JsonSerializer.Serialize(command);

    [Fact]
    public void Tools_AreNamedAndTheShellAndDelegateOfferBackgroundWork()
    {
        Assert.Equal(["task_wait", "task_output", "task_stop"], new PromptLibraryTools().TaskNames);
        Assert.Equal(["task_output", "task_stop", "task_wait"], following.Keys.Order(StringComparer.Ordinal));
        Assert.True(shell.InputSchema.GetProperty("properties").TryGetProperty("background", out JsonElement _));
        Assert.True(delegation.InputSchema.GetProperty("properties").TryGetProperty("background", out JsonElement _));
        Assert.Contains("background", shell.Description);
        Assert.Contains("task_wait", delegation.Description);
        Assert.Equal(["task_ids", "timeout_seconds"], following["task_wait"].InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["task_id"], following["task_stop"].InputSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public async Task ABackgroundCommand_ReturnsAtOnceWithATaskIdentifierAndKeepsRunning()
    {
        DateTime begun = DateTime.UtcNow;

        string started = await Call(shell, $$"""{"command":{{Escaped("sleep 1; echo done > ran.txt")}},"background":true}""");

        Assert.True(DateTime.UtcNow - begun < TimeSpan.FromSeconds(1));
        Assert.StartsWith("Started task-1 in the background", started);
        Assert.Contains("task_wait", started);
        Assert.False(File.Exists(temp.Combine("ran.txt")));

        string report = await Call(following["task_wait"], """{"timeout_seconds":20}""");

        Assert.Contains("task-1 ended with exit code 0", report);
        Assert.True(File.Exists(temp.Combine("ran.txt")));
    }

    [Fact]
    public async Task Wait_ReturnsWhenTheTimeIsUpWithHowLongTheTaskRunsAndItsLatestOutput()
    {
        await Call(shell, $$"""{"command":{{Escaped("echo first; echo second; sleep 30")}},"background":true}""");
        await Task.Delay(500);

        DateTime begun = DateTime.UtcNow;
        string report = await Call(following["task_wait"], """{"timeout_seconds":1}""");

        Assert.InRange(DateTime.UtcNow - begun, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(10));
        Assert.Contains("task-1 is still running after", report);
        Assert.Contains("first\nsecond", report);
        Assert.Contains("echo first", report);
        Assert.Contains("If you owe the user a status update, write it now", report);
    }

    [Fact]
    public async Task Wait_ReturnsAsSoonAsOneOfTheTasksEndsAndReportsTheOthersAsRunning()
    {
        await Call(shell, $$"""{"command":{{Escaped("sleep 30")}},"background":true}""");
        await Call(shell, $$"""{"command":{{Escaped("echo quick")}},"background":true}""");

        DateTime begun = DateTime.UtcNow;
        string report = await Call(following["task_wait"], """{"timeout_seconds":30}""");

        Assert.True(DateTime.UtcNow - begun < TimeSpan.FromSeconds(10));
        Assert.Contains("task-2 ended with exit code 0 after", report);
        Assert.Contains("quick", report);
        Assert.Contains("task-1 is still running", report);
        Assert.DoesNotContain("status update", report);
    }

    [Fact]
    public async Task Wait_ReportsAnEndedTaskOnlyOnceAndSaysSoWhenNothingIsLeft()
    {
        await Call(shell, $$"""{"command":{{Escaped("echo once")}},"background":true}""");
        await Call(following["task_wait"], """{"timeout_seconds":20}""");

        Assert.Equal("There are no background tasks to wait for.", await Call(following["task_wait"], """{"timeout_seconds":0}"""));
        Assert.Contains("already reported", await Call(following["task_wait"], """{"task_ids":["task-1"],"timeout_seconds":0}"""));
    }

    [Fact]
    public async Task Wait_WithIdsWatchesOnlyThoseAndRefusesAnUnknownOne()
    {
        await Call(shell, $$"""{"command":{{Escaped("sleep 30")}},"background":true}""");
        await Call(shell, $$"""{"command":{{Escaped("echo two")}},"background":true}""");

        string report = await Call(following["task_wait"], """{"task_ids":["task-2"],"timeout_seconds":20}""");

        Assert.Contains("task-2 ended", report);
        Assert.DoesNotContain("task-1", report);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(following["task_wait"], """{"task_ids":["task-9"]}"""));
    }

    [Fact]
    public async Task Output_ReadsTheLatestLinesWithoutWaitingAndRefusesAHelper()
    {
        await Call(shell, $$"""{"command":{{Escaped("for i in 1 2 3 4 5; do echo line$i; done; sleep 30")}},"background":true}""");
        await Call(delegation, """{"task":"slow","background":true}""");
        await Task.Delay(500);

        Assert.Equal("line4\nline5", await Call(following["task_output"], """{"task_id":"task-1","lines":2}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(following["task_output"], """{"task_id":"task-2"}"""));
    }

    [Fact]
    public async Task Stop_EndsACommandAndEverythingItStartedAndWaitReportsItStopped()
    {
        string marker = temp.Combine("alive.txt");
        await Call(shell, $$"""{"command":{{Escaped($"(while true; do date > {marker}; sleep 0.2; done) & sleep 60")}},"background":true}""");
        await Task.Delay(700);
        Assert.True(File.Exists(marker));

        string stopping = await Call(following["task_stop"], """{"task_id":"task-1"}""");
        string report = await Call(following["task_wait"], """{"timeout_seconds":10}""");
        DateTime last = File.GetLastWriteTimeUtc(marker);
        await Task.Delay(800);

        Assert.Equal("Stopping task-1.", stopping);
        Assert.Contains("task-1 was stopped", report);
        Assert.Equal(last, File.GetLastWriteTimeUtc(marker));
        Assert.Equal("task-1 had already ended.", await Call(following["task_stop"], """{"task_id":"task-1"}"""));
    }

    [Fact]
    public async Task ABackgroundCommand_IsStoppedWhenItsTimeoutPasses()
    {
        await Call(shell, $$"""{"command":{{Escaped("sleep 30")}},"background":true,"timeout_seconds":1}""");

        string report = await Call(following["task_wait"], """{"timeout_seconds":20}""");

        Assert.Contains("timed out and was stopped (limit 1 seconds)", report);
    }

    [Fact]
    public async Task ACommandThatFails_IsReportedWithItsExitCode()
    {
        await Call(shell, $$"""{"command":{{Escaped("echo bad >&2; exit 3")}},"background":true}""");

        string report = await Call(following["task_wait"], """{"timeout_seconds":20}""");

        Assert.Contains("task-1 ended with exit code 3", report);
        Assert.Contains("bad", report);
    }

    [Fact]
    public async Task ABackgroundHelper_ReturnsAtOnceAndItsAnswerComesWithWait()
    {
        string started = await Call(delegation, """{"task":"look into x","background":true}""");
        string report = await Call(following["task_wait"], """{"timeout_seconds":20}""");

        Assert.StartsWith("Started task-1 in the background: a helper works", started);
        Assert.Contains("task-1 finished", report);
        Assert.Contains("helper did look into x", report);
    }

    [Fact]
    public async Task ABackgroundHelper_ThatFailsOrIsStoppedIsReportedAsSuch()
    {
        await Call(delegation, """{"task":"fail","background":true}""");
        await Call(delegation, """{"task":"slow","background":true}""");
        string failed = await Call(following["task_wait"], """{"task_ids":["task-1"],"timeout_seconds":20}""");

        await Call(following["task_stop"], """{"task_id":"task-2"}""");
        string stopped = await Call(following["task_wait"], """{"timeout_seconds":20}""");

        Assert.Contains("task-1 failed", failed);
        Assert.Contains("the helper broke", failed);
        Assert.Contains("task-2 was stopped", stopped);
    }

    [Fact]
    public async Task ABlockingDelegateAndAForegroundCommandStillWaitForTheirResult()
    {
        Assert.Equal("helper did now", await Call(delegation, """{"task":"now"}"""));
        Assert.Equal("fg\n(exit code 0)", await Call(shell, """{"command":"echo fg"}"""));
        Assert.Equal("There are no background tasks to wait for.", await Call(following["task_wait"], """{"timeout_seconds":0}"""));
    }

    [Fact]
    public async Task StopAll_AndDisposeEndEveryRunningTask()
    {
        await Call(shell, $$"""{"command":{{Escaped("sleep 60")}},"background":true}""");
        await Call(delegation, """{"task":"slow","background":true}""");

        tasks.StopAll();
        string report = await Call(following["task_wait"], """{"timeout_seconds":20}""");
        report += await Call(following["task_wait"], """{"timeout_seconds":20}""");
        await tasks.DisposeAsync();

        Assert.Contains("task-1 was stopped", report);
        Assert.Contains("task-2 was stopped", report);
    }

    [Fact]
    public async Task TooManyRunningTasks_AreRefusedUntilSomeEnd()
    {
        for (int index = 0; index < 16; index++)
        {
            await Call(shell, $$"""{"command":{{Escaped("sleep 60")}},"background":true}""");
        }

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(shell, $$"""{"command":{{Escaped("sleep 60")}},"background":true}"""));
        await Call(following["task_stop"], """{"task_id":"task-1"}""");
        await Call(following["task_wait"], """{"task_ids":["task-1"],"timeout_seconds":20}""");

        Assert.Contains("16 background tasks are running", refusal.Message);
        Assert.StartsWith("Started task-17", await Call(shell, $$"""{"command":{{Escaped("sleep 60")}},"background":true}"""));
    }

    [Fact]
    public async Task ACommandThatCannotBeStarted_IsRefusedAndLeavesNoTask()
    {
        PromptTool broken = new PromptLibraryTools().CreateShell(temp.Path, tasks);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(broken, $$"""{"command":"echo hi","working_directory":{{Escaped(temp.Combine("missing"))}},"background":true}"""));

        Assert.Equal("There are no background tasks to wait for.", await Call(following["task_wait"], """{"timeout_seconds":0}"""));
    }
}
