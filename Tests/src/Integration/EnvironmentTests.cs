namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class EnvironmentTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private string Claude(string name, string says)
        => temp.Script(name, $"mkdir -p \"$CLAUDE_CONFIG_DIR/projects/p\"\n: > \"$CLAUDE_CONFIG_DIR/projects/p/claude-session.jsonl\"\nIFS= read -r line\necho '{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{says}\"}}]}}}}'\necho '{{\"type\":\"result\",\"is_error\":false,\"session_id\":\"claude-session\"}}'");

    [Fact]
    public async Task ThePrompterRunsTheToolTheEnvironmentNames()
    {
        Prompter prompter = new(temp.Combine("data")) { Environment = new PromptEnvironment { ClaudeCommand = Claude("claude-one", "from one"), ClaudeConfigFolder = temp.Combine("claude-state") } };
        IPromptSession session = temp.Track(await prompter.Create(new ClaudeSetup()));

        Assert.Equal("from one", await session.Request("hi"));
    }

    [Fact]
    public async Task TheEnvironmentIsNotStoredWithTheSessionSoALoadRunsTheToolOfTheLoadingPrompter()
    {
        string configFolder = temp.Combine("claude-state");
        Prompter first = new(temp.Combine("data")) { Environment = new PromptEnvironment { ClaudeCommand = Claude("claude-one", "from one"), ClaudeConfigFolder = configFolder } };
        IPromptSession created = temp.Track(await first.Create(new ClaudeSetup()));
        await created.Request("hi");
        await created.DisposeAsync();
        Prompter second = new(temp.Combine("data")) { Environment = new PromptEnvironment { ClaudeCommand = Claude("claude-two", "from two"), ClaudeConfigFolder = configFolder } };

        IPromptSession loaded = temp.Track(await second.Load(created.Id));

        string stored = await File.ReadAllTextAsync(temp.Combine($"data/Sessions/{created.Id}.json"));
        Assert.DoesNotContain("claude-one", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-state", stored, StringComparison.Ordinal);
        Assert.Equal("from two", await loaded.Request("again"));
    }

    [Fact]
    public async Task TheConfigurationOfTheEnvironmentStartsASessionAndALoadedSessionKeepsWhatItWasLeftWith()
    {
        Prompter prompter = new(temp.Combine("data")) { Environment = new PromptEnvironment { ClaudeCommand = Claude("claude-one", "from one"), ClaudeConfigFolder = temp.Combine("claude-state"), Capability = PromptCapability.Strong, Effort = PromptEffort.High, IsShellAllowed = true, IsSystemAccessAllowed = true, Instructions = "Be brief." } };
        IPromptSession created = temp.Track(await prompter.Create(new ClaudeSetup()));

        Assert.Equal((PromptCapability.Strong, PromptEffort.High, true, true, "Be brief."), (created.Configuration.Capability, created.Configuration.Effort, created.Configuration.IsShellAllowed, created.Configuration.IsSystemAccessAllowed, created.Configuration.Instructions));
        created.Configuration.Capability = PromptCapability.Fast;
        await created.DisposeAsync();
        IPromptSession loaded = temp.Track(await prompter.Load(created.Id));

        Assert.Equal((PromptCapability.Fast, PromptEffort.High, true, true, "Be brief."), (loaded.Configuration.Capability, loaded.Configuration.Effort, loaded.Configuration.IsShellAllowed, loaded.Configuration.IsSystemAccessAllowed, loaded.Configuration.Instructions));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task TheImageToolIsOfferedWhenTheSessionAllowsItAndTheEnvironmentHasAnImageSetup(bool allowed, bool configured, bool offered)
    {
        string script = temp.Script("claude", "IFS= read -r line\nprintf '%s\\n' \"$@\" > \"$PWD/args.txt\"\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\"}'");
        Prompter prompter = new(temp.Combine("data")) { Environment = new PromptEnvironment { ClaudeCommand = script, ClaudeConfigFolder = temp.Combine("claude-state"), IsImageGenerationAllowed = allowed, ImageGeneration = configured ? new OpenAiImageSetup { ApiKey = "sk-test" } : null } };
        IPromptSession session = temp.Track(await prompter.Create(new ClaudeSetup()));

        await session.Request("go");

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Equal(offered, arguments[Array.IndexOf(arguments, "--allowedTools") + 1].Split(',').Contains("mcp__liveai__generate_image"));
    }

    [Fact]
    public async Task AnImageSetupSetOnTheEnvironmentAfterTheSessionWasCreatedReachesItsNextPrompt()
    {
        string script = temp.Script("claude", "IFS= read -r line\nprintf '%s\\n' \"$@\" > \"$PWD/args.txt\"\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\"}'");
        Prompter prompter = new(temp.Combine("data")) { Environment = new PromptEnvironment { ClaudeCommand = script, ClaudeConfigFolder = temp.Combine("claude-state"), IsImageGenerationAllowed = true } };
        IPromptSession session = temp.Track(await prompter.Create(new ClaudeSetup()));
        await session.Request("one");

        prompter.Environment = prompter.Environment with { ImageGeneration = new OpenAiImageSetup { ApiKey = "sk-test" } };
        await session.Request("two");

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Contains("mcp__liveai__generate_image", arguments[Array.IndexOf(arguments, "--allowedTools") + 1].Split(','));
    }

    [Fact]
    public async Task TheConfigurationOfTheEnvironmentIsWhatNewSessionsStartWithAndAChangeLaterDoesNotReachTheOnesThatExist()
    {
        Prompter prompter = new(temp.Combine("data")) { Environment = new PromptEnvironment { ClaudeCommand = Claude("claude-one", "from one"), ClaudeConfigFolder = temp.Combine("claude-state"), Capability = PromptCapability.Strong, Effort = PromptEffort.Low, IsWebAllowed = true } };
        IPromptSession created = temp.Track(await prompter.Create(new ClaudeSetup()));

        Assert.Equal((PromptCapability.Strong, PromptEffort.Low, true, false, false), (created.Configuration.Capability, created.Configuration.Effort, created.Configuration.IsWebAllowed, created.Configuration.IsSystemAccessAllowed, created.Configuration.IsShellAllowed));
        created.Configuration.Capability = PromptCapability.Fast;
        await created.DisposeAsync();
        prompter.Environment = prompter.Environment with { Capability = PromptCapability.Max, IsWebAllowed = false, IsSystemAccessAllowed = true };
        IPromptSession loaded = temp.Track(await prompter.Load(created.Id));
        IPromptSession another = temp.Track(await prompter.Create(new ClaudeSetup()));

        Assert.Equal((PromptCapability.Fast, PromptEffort.Low, true, false), (loaded.Configuration.Capability, loaded.Configuration.Effort, loaded.Configuration.IsWebAllowed, loaded.Configuration.IsSystemAccessAllowed));
        Assert.Equal((PromptCapability.Max, false, true), (another.Configuration.Capability, another.Configuration.IsWebAllowed, another.Configuration.IsSystemAccessAllowed));
    }
}
