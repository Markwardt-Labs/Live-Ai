namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ConfigurationAcrossToolsTests : IAsyncLifetime
{
    public static TheoryData<string> Tools => ["claude", "codex", "local"];

    private readonly TempFolder temp = new();

    private readonly FakeOpenCodeServer server = new();

    private readonly Dictionary<Type, IPromptEngine> engines = [];

    private readonly FakeModelServer ollama = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "gemma4:26b", "capabilities": ["completion", "tools"] }] }""");

    public Task InitializeAsync()
    {
        server.OnPrompt = _ => server.Reply("ok");
        string claude = temp.Script("claude", "mkdir -p \"$CLAUDE_CONFIG_DIR/projects/p\"\n: > \"$CLAUDE_CONFIG_DIR/projects/p/claude-session.jsonl\"\nIFS= read -r line\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"claude says hi\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"claude-session\"}'");
        engines[typeof(ClaudeSetup)] = new ClaudeEngine(claude, temp.Combine("claude-state"));
        engines[typeof(CodexSetup)] = new CodexEngine(new FakeCodex(temp).Script(), temp.Combine("codex-state"));
        engines[typeof(LocalSetup)] = new OpenCodeEngine(new FakeOpenCode(temp).Script(server), [ollama.Api], null, PromptCapabilityModels.All("gemma4:26b"), PromptCapabilityContexts.All(8192)) { ConnectWait = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromMilliseconds(20) };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        ollama.Dispose();
        temp.Dispose();
    }

    private PromptSetup Options(string tool, string workspace)
        => tool switch
        {
            "claude" => new ClaudeSetup { WorkspacePath = workspace, ApiKey = "key" },
            "codex" => new CodexSetup { WorkspacePath = workspace, ApiKey = "key" },
            _ => new LocalSetup { WorkspacePath = workspace, Overrides = new PromptServerSettings { Models = PromptCapabilityModels.All("gemma4:26b"), Contexts = PromptCapabilityContexts.All(8192) } }
        };

    private Prompter Prompter()
        => TestProviders.MakePrompter(options => engines[options.GetType()], temp.Combine("data"));

    private void Configure(IPromptSessionConfiguration configuration, int variant)
    {
        configuration.Capability = variant == 0 ? PromptCapability.Strong : PromptCapability.Fast;
        configuration.Effort = variant == 0 ? PromptEffort.High : PromptEffort.Low;
        configuration.IsShellAllowed = variant == 0;
        configuration.IsWebAllowed = variant == 0;
        configuration.IsSandboxed = variant != 0;
        configuration.Instructions = variant == 0 ? "Be brief." : "Be thorough.";
        configuration.IsImageGenerationAllowed = variant == 0;
    }

    private (PromptCapability, PromptEffort, bool, bool, bool, string?, bool) Read(IPromptSessionConfiguration configuration)
        => (configuration.Capability, configuration.Effort, configuration.IsShellAllowed, configuration.IsWebAllowed, configuration.IsSandboxed, configuration.Instructions, configuration.IsImageGenerationAllowed);

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task ASessionOfAnyToolIsCreatedEditedAndLoadedWithItsWholeConfiguration(string tool)
    {
        string workspace = Directory.CreateDirectory(temp.Combine("project")).FullName;
        Prompter prompter = Prompter();
        IPromptSession created = temp.Track(await prompter.Create(Options(tool, workspace)));
        Configure(created.Configuration, 0);
        Assert.False(string.IsNullOrEmpty(await created.Request("first")));
        (PromptCapability, PromptEffort, bool, bool, bool, string?, bool) first = Read(created.Configuration);

        Configure(created.Configuration, 1);
        (PromptCapability, PromptEffort, bool, bool, bool, string?, bool) second = Read(created.Configuration);
        await created.DisposeAsync();
        IPromptSession loaded = temp.Track(await prompter.Load(created.Id));

        Assert.NotEqual(first, second);
        Assert.Equal(second, Read(loaded.Configuration));
        Assert.Equal(workspace, loaded.WorkspacePath);
        Assert.False(string.IsNullOrEmpty(await loaded.Request("second")));
        Configure(loaded.Configuration, 0);
        await loaded.DisposeAsync();
        IPromptSession again = temp.Track(await prompter.Load(created.Id));
        Assert.Equal(first, Read(again.Configuration));
        await again.DisposeAsync();
        Assert.True(await prompter.Delete(created.Id));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task ASessionOfAnyToolCanBeEditedBeforeItsFirstPromptAndLoadedWithoutOne(string tool)
    {
        Prompter prompter = Prompter();
        IPromptSession created = temp.Track(await prompter.Create(Options(tool, Directory.CreateDirectory(temp.Combine("project")).FullName)));
        Configure(created.Configuration, 1);
        (PromptCapability, PromptEffort, bool, bool, bool, string?, bool) expected = Read(created.Configuration);
        await created.DisposeAsync();

        IPromptSession loaded = temp.Track(await prompter.Load(created.Id));

        Assert.Equal(expected, Read(loaded.Configuration));
        await loaded.DisposeAsync();
    }
}
