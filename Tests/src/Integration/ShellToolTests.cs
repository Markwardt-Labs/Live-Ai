namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ShellToolTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly PromptTool shell;

    public ShellToolTests()
        => shell = new PromptLibraryTools().CreateShell(temp.Path, new PromptTasks());

    public void Dispose()
        => temp.Dispose();

    private Task<string> Run(string command, CancellationToken cancellation = default)
        => RunJson(JsonSerializer.Serialize(new { command }), cancellation);

    private Task<string> RunJson(string json, CancellationToken cancellation)
        => shell.Handler(JsonDocument.Parse(json).RootElement, cancellation);

    [Fact]
    public void Tool_IsNamedShellAndDescribesItsArguments()
    {
        JsonElement properties = shell.InputSchema.GetProperty("properties");

        Assert.Equal("shell", shell.Name);
        Assert.Equal("shell", new PromptLibraryTools().ShellName);
        Assert.True(properties.TryGetProperty("command", out JsonElement _));
        Assert.True(properties.TryGetProperty("working_directory", out JsonElement _));
        Assert.True(properties.TryGetProperty("timeout_seconds", out JsonElement _));
        Assert.Equal(["command"], shell.InputSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.DoesNotContain(new PromptLibraryTools().Names, name => name == "shell");
    }

    [Fact]
    public async Task Run_ReturnsTheOutputAndTheExitCode()
        => Assert.Equal("hello\n(exit code 0)", await Run("echo hello"));

    [Fact]
    public async Task Run_IncludesStandardErrorAndAFailingExitCode()
    {
        string result = await Run("echo out; echo err 1>&2; exit 3");

        Assert.Contains("out", result);
        Assert.Contains("err", result);
        Assert.EndsWith("(exit code 3)", result);
    }

    [Fact]
    public async Task Run_WithNoOutput_ReturnsJustTheExitCode()
        => Assert.Equal("(exit code 0)", await Run("true"));

    [Fact]
    public async Task Run_StartsInTheGivenFolderUnlessTheAiNamesAnother()
    {
        Directory.CreateDirectory(temp.Combine("other"));

        Assert.Equal($"{temp.Path}\n(exit code 0)", await Run("pwd"));
        Assert.Equal($"{temp.Combine("other")}\n(exit code 0)", await RunJson(JsonSerializer.Serialize(new { command = "pwd", working_directory = temp.Combine("other") }), default));
    }

    [Fact]
    public async Task Run_GivesTheCommandNoInputToWaitFor()
        => Assert.Equal("(exit code 0)", await Run("cat"));

    [Fact]
    public async Task Run_CanDoAnythingTheUserCan()
    {
        await Run("echo made > made.txt && mkdir sub && cp made.txt sub/copy.txt");

        Assert.Equal("made\n", await File.ReadAllTextAsync(temp.Combine("sub/copy.txt")));
    }

    [Fact]
    public async Task Run_StopsACommandThatRunsPastItsTimeout()
    {
        DateTime begun = DateTime.UtcNow;

        string result = await RunJson(JsonSerializer.Serialize(new { command = "echo before; sleep 20", timeout_seconds = 1 }), default);

        Assert.Contains("before", result);
        Assert.EndsWith("(timed out after 1 seconds and stopped)", result);
        Assert.True(DateTime.UtcNow - begun < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Run_StopsTheCommandWhenTheCallIsCancelled()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));
        DateTime begun = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("sleep 20", cancellation.Token));

        Assert.True(DateTime.UtcNow - begun < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Run_CutsVeryLongOutput()
    {
        string result = await Run("head -c 300000 /dev/zero | tr '\\0' 'a' | fold -w 100");

        Assert.Contains("(output cut at 64 KB)", result);
        Assert.True(result.Length < 80000);
        Assert.EndsWith("(exit code 0)", result);
    }

    [Fact]
    public async Task Run_DoesNotWaitForABackgroundProcessThatKeepsTheOutputOpen()
    {
        PromptTool quick = new PromptLibraryTools { ShellPipeGrace = TimeSpan.FromMilliseconds(100) }.CreateShell(temp.Path, new PromptTasks());
        DateTime begun = DateTime.UtcNow;

        string result = await Call(quick, new { command = "sleep 3 & echo started" });

        Assert.Equal("started\n(exit code 0)", result);
        Assert.True(DateTime.UtcNow - begun < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_InAFolderThatDoesNotExist_ThrowsWithAClearMessage()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunJson(JsonSerializer.Serialize(new { command = "true", working_directory = temp.Combine("missing") }), default));

        Assert.StartsWith("The command could not be started", exception.Message);
    }

    [Fact]
    public async Task Run_ClampsTheTimeoutToAtLeastOneSecond()
    {
        string result = await RunJson(JsonSerializer.Serialize(new { command = "echo quick", timeout_seconds = -5 }), default);

        Assert.Equal("quick\n(exit code 0)", result);
    }

    private Task<string> Call(PromptTool tool, object arguments)
        => tool.Handler(JsonSerializer.SerializeToElement(arguments), default);
}
