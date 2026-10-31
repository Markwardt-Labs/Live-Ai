namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class TurnRunnerTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly TurnRunner runner = new();

    private readonly Mock<IPromptEngine> engine = new();

    public TurnRunnerTests()
    {
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.Parse(It.IsAny<string>())).Returns((string line) => line.StartsWith("reply ") ? [new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = line[6..] }] : line == "fail" ? [new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = "reported" }] : []);
    }

    public void Dispose()
        => temp.Dispose();

    private ProcessStartInfo Start(string body)
        => new(temp.Script("tool", body)) { WorkingDirectory = temp.Path };

    private Task<List<PromptEngineEvent>> Run(ProcessStartInfo start, string prompt = "prompt", CancellationToken cancellation = default)
        => runner.Run(engine.Object, start, prompt, cancellation).ToListAsync(cancellation).AsTask();

    [Fact]
    public async Task Run_WritesThePromptToInputAndReportsWhatTheProviderParses()
    {
        List<PromptEngineEvent> events = await Run(Start("cat > \"$PWD/stdin.txt\"\necho 'reply first'\necho noise\necho 'reply second'"), "héllo\nworld");

        Assert.Equal(["first", "second"], events.Select(output => output.Text));
        Assert.Equal("héllo\nworld", await File.ReadAllTextAsync(temp.Combine("stdin.txt")));
    }

    [Fact]
    public async Task Run_WhenTheProcessFailsWithoutSayingWhy_ReportsItsErrorOutput()
    {
        List<PromptEngineEvent> events = await Run(Start("cat > /dev/null\necho 'reply partial'\necho 'it broke' >&2\nexit 3"));

        Assert.Equal([PromptEngineEventKind.Reply, PromptEngineEventKind.Failed], events.Select(output => output.Kind));
        Assert.Equal("it broke", events[1].Text);
    }

    [Fact]
    public async Task Run_WhenTheProcessFailsSilently_NamesTheProvider()
    {
        List<PromptEngineEvent> events = await Run(Start("cat > /dev/null\nexit 1"));

        Assert.Equal("Fake stopped.", Assert.Single(events).Text);
    }

    [Fact]
    public async Task Run_WhenTheProviderReportedTheFailure_DoesNotAddAnother()
    {
        List<PromptEngineEvent> events = await Run(Start("cat > /dev/null\necho fail\necho 'noise' >&2\nexit 1"));

        Assert.Equal("reported", Assert.Single(events).Text);
    }

    [Fact]
    public async Task Run_WhenTheProcessIgnoresItsInput_StillReadsItsOutput()
    {
        string big = new('x', 4 * 1024 * 1024);

        List<PromptEngineEvent> events = await Run(Start("echo 'reply early'"), big);

        Assert.Equal("early", Assert.Single(events).Text);
    }

    [Fact]
    public async Task Run_WhenTheToolIsMissing_Throws()
    {
        ProcessStartInfo start = new(temp.Combine("missing"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptToolStartException>(() => Run(start));

        Assert.Contains("Fake could not be started", exception.Message);
        Assert.Contains(start.FileName, exception.Message);
    }

    [Fact]
    public async Task Run_WhenCancelled_EndsTheProcessAndItsChildren()
    {
        string marker = temp.Combine("child.pid");
        ProcessStartInfo start = Start($"cat > /dev/null\nsleep 300 &\necho $! > '{marker}'\nwait");
        using CancellationTokenSource cancellation = new();

        Task<List<PromptEngineEvent>> running = Run(start, cancellation: cancellation.Token);
        await WaitFor(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
        int child = int.Parse((await File.ReadAllTextAsync(marker)).Trim());
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await WaitFor(() => !Alive(child));
        Assert.False(Alive(child));
    }

    [Fact]
    public async Task Run_WhenTheReaderStopsEarly_EndsTheProcess()
    {
        string marker = temp.Combine("pid");
        ProcessStartInfo start = Start($"cat > /dev/null\necho $$ > '{marker}'\necho 'reply one'\nsleep 300");

        await foreach (PromptEngineEvent output in runner.Run(engine.Object, start, "p", CancellationToken.None))
        {
            break;
        }

        int pid = int.Parse((await File.ReadAllTextAsync(marker)).Trim());
        await WaitFor(() => !Alive(pid));
        Assert.False(Alive(pid));
    }

    private bool Alive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }
    }
}
