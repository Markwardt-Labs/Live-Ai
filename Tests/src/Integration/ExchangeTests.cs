namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ExchangeTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly TurnRunner runner = new();

    public void Dispose()
        => temp.Dispose();

    private ProcessStartInfo Start(string body)
        => new(temp.Script("tool", body));

    [Fact]
    public async Task Exchange_WritesTheLinesAndReadsTheOutputUntilTheProcessEnds()
    {
        List<string> lines = await runner.Exchange("Tool", Start("while IFS= read -r line; do echo \"got $line\"; done"), ["a", "b"], true).ToListAsync();

        Assert.Equal(["got a", "got b"], lines);
    }

    [Fact]
    public async Task Exchange_WithoutClosingTheInput_CanBeStoppedEarlyWhichEndsTheProcess()
    {
        IAsyncEnumerable<string> output = runner.Exchange("Tool", Start("echo $$ > \"$PWD/pid.txt\"\nwhile IFS= read -r line; do echo \"got $line\"; done"), ["a"], false);

        await foreach (string line in output)
        {
            Assert.Equal("got a", line);
            break;
        }

        int pid = int.Parse(await File.ReadAllTextAsync(temp.Combine("pid.txt")));

        for (int attempt = 0; attempt < 100 && Alive(pid); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.False(Alive(pid));
    }

    [Fact]
    public async Task Exchange_WhenTheProcessFails_ThrowsItsErrorOutput()
    {
        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => runner.Exchange("Tool", Start("echo broken >&2\nexit 2"), [], true).ToListAsync().AsTask());

        Assert.Equal("broken", exception.Message);
    }

    [Fact]
    public async Task Exchange_WhenTheProcessFailsSilently_NamesTheTool()
    {
        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => runner.Exchange("Tool", Start("exit 2"), [], true).ToListAsync().AsTask());

        Assert.Equal("Tool stopped.", exception.Message);
    }

    [Fact]
    public async Task Exchange_WhenTheProcessCannotBeStarted_Throws()
        => await Assert.ThrowsAsync<PromptToolStartException>(() => runner.Exchange("Tool", new ProcessStartInfo(temp.Combine("missing")), [], true).ToListAsync().AsTask());

    [Fact]
    public async Task Exchange_WhenTheProcessIgnoresItsInput_StillReadsItsOutput()
    {
        List<string> lines = await runner.Exchange("Tool", Start("echo done"), ["ignored"], true).ToListAsync();

        Assert.Equal(["done"], lines);
    }

    [Fact]
    public async Task Exchange_WhenCancelled_EndsTheProcessAndThrows()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.Exchange("Tool", Start("sleep 30"), [], true, cancellation.Token).ToListAsync().AsTask());
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
}
