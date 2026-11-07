namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ToolServiceTestTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptToolService Claude(string? script = null)
        => TestServices.Make(new ClaudeEngine(script is null ? temp.Combine("missing-claude") : temp.Script("claude", "case \" $* \" in *\" /usage \"*) exit 0 ;; esac\n" + script), temp.Combine("claude-state")), temp.Combine("data"));

    private IPromptToolService Codex(string? script = null)
        => TestServices.Make(new CodexEngine(script is null ? temp.Combine("missing-codex") : temp.Script("codex", "if [ \"$1\" = \"app-server\" ]; then exit 0; fi\n" + script), temp.Combine("codex-state")), temp.Combine("data"));

    private string Record(string name)
        => $"printf '%s\\n' \"$@\" > \"$PWD/{name}-args.txt\"\nprintf '%s' \"$ANTHROPIC_API_KEY|$CODEX_API_KEY\" > \"$PWD/{name}-key.txt\"\ncat > \"$PWD/{name}-stdin.txt\"\n";

    [Fact]
    public async Task Claude_IsAvailableWhenItAnswersAndIsAskedAsLittleAsPossible()
    {
        IPromptToolService claude = Claude(Record("claude") + "echo '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"1\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\"}'");

        PromptTestResult result = await claude.Test();

        Assert.True(result.IsAvailable);
        Assert.Null(result.Error);
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        Assert.Equal("haiku", arguments[Array.IndexOf(arguments, "--model") + 1]);
        Assert.Equal("low", arguments[Array.IndexOf(arguments, "--effort") + 1]);
        Assert.Equal("Be terse.", arguments[Array.IndexOf(arguments, "--system-prompt") + 1]);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Equal(["--tools", ""], arguments.TakeLast(2));
        Assert.Equal("Reply with the digit 1.", await File.ReadAllTextAsync(temp.Combine("claude-stdin.txt")));
        Assert.Equal("|", await File.ReadAllTextAsync(temp.Combine("claude-key.txt")));
    }

    [Fact]
    public async Task Codex_IsAvailableWhenItAnswersOnTheModelItDescribesAsFastAndStoresNothing()
    {
        string catalog = "if [ \"$1\" = \"debug\" ]; then echo '{\"models\":[{\"slug\":\"gpt-big\",\"visibility\":\"list\",\"description\":\"Frontier model.\",\"supported_reasoning_levels\":[{\"effort\":\"low\"},{\"effort\":\"high\"}]},{\"slug\":\"gpt-quick\",\"visibility\":\"list\",\"description\":\"Older fast and efficient model.\",\"supported_reasoning_levels\":[{\"effort\":\"low\"},{\"effort\":\"medium\"}]}]}'; exit 0; fi\n";
        IPromptToolService codex = Codex(catalog + Record("codex") + "echo '{\"type\":\"thread.started\",\"thread_id\":\"t\"}'\necho '{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"1\"}}'\necho '{\"type\":\"turn.completed\"}'");

        PromptTestResult result = await codex.Test();

        Assert.True(result.IsAvailable);
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("codex-args.txt"));
        Assert.Equal("gpt-quick", arguments[Array.IndexOf(arguments, "-m") + 1]);
        Assert.Contains("--ephemeral", arguments);
        Assert.Contains("model_reasoning_effort=\"low\"", arguments);
        Assert.Equal("-", arguments[^1]);
        Assert.Equal("Reply with the digit 1.", await File.ReadAllTextAsync(temp.Combine("codex-stdin.txt")));
    }

    [Fact]
    public async Task TheApiKeyIsGivenToTheToolThroughItsEnvironment()
    {
        IPromptToolService claude = Claude(Record("claude") + "echo '{\"type\":\"result\",\"is_error\":false}'");
        IPromptToolService codex = Codex(Record("codex") + "echo '{\"type\":\"turn.completed\"}'");

        await claude.Test("claude-key");
        await codex.Test("codex-key");

        Assert.Equal("claude-key|", await File.ReadAllTextAsync(temp.Combine("claude-key.txt")));
        Assert.Equal("|codex-key", await File.ReadAllTextAsync(temp.Combine("codex-key.txt")));
        Assert.DoesNotContain("claude-key", string.Join(' ', await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"))));
    }

    [Fact]
    public async Task AFailureOfTheAi_IsAnUnavailableResultWithItsReason()
    {
        IPromptToolService claude = Claude("cat > /dev/null\necho '{\"type\":\"result\",\"is_error\":true,\"result\":\"Invalid API key\"}'");
        IPromptToolService codex = Codex("cat > /dev/null\necho '{\"type\":\"turn.failed\",\"error\":{\"message\":\"not signed in\"}}'");

        PromptTestResult claudeResult = await claude.Test("bad");
        PromptTestResult codexResult = await codex.Test();

        Assert.False(claudeResult.IsAvailable);
        Assert.Equal("Invalid API key", claudeResult.Error);
        Assert.False(codexResult.IsAvailable);
        Assert.Equal("not signed in", codexResult.Error);
    }

    [Fact]
    public async Task AToolThatCannotBeStarted_IsUnavailable()
    {
        PromptTestResult result = await Claude().Test();

        Assert.False(result.IsAvailable);
        Assert.Contains("could not be started", result.Error);
    }

    [Fact]
    public async Task AToolThatCrashesOrEndsWithoutAnswering_IsUnavailable()
    {
        IPromptToolService claude = Claude("cat > /dev/null\necho oops >&2\nexit 3");
        IPromptToolService codex = Codex("cat > /dev/null\nexit 0");

        PromptTestResult crashed = await claude.Test();
        PromptTestResult silent = await codex.Test();

        Assert.Equal("oops", crashed.Error);
        Assert.False(silent.IsAvailable);
        Assert.Contains("ended without answering", silent.Error);
    }

    [Fact]
    public async Task AToolThatKeepsRunningAfterItAnswers_IsAvailableAtOnceAndStopped()
    {
        IPromptToolService claude = Claude("cat > /dev/null\necho $$ > \"$PWD/pid.txt\"\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"1\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\"}'\nsleep 120");
        Stopwatch watch = Stopwatch.StartNew();

        PromptTestResult result = await claude.Test();

        Assert.True(result.IsAvailable);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
        int pid = int.Parse((await File.ReadAllTextAsync(temp.Combine("pid.txt"))).Trim());

        for (int attempt = 0; attempt < 100 && Directory.Exists($"/proc/{pid}"); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.False(Directory.Exists($"/proc/{pid}"));
    }

    [Fact]
    public async Task AToolThatNeverAnswers_IsWaitedForUntilTheCallerCancels()
    {
        IPromptToolService claude = Claude("cat > /dev/null\nsleep 120");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claude.Test(cancellation: cancellation.Token));
    }
}
