namespace Markwardt.LiveAi.Tests.Unit;

public sealed class EngineProviderTests
{
    [Fact]
    public async Task AProviderOfClaudeUsesTheCommandTheConfigFolderAndTheDataFolderItIsGiven()
    {
        using TempFolder temp = new();
        string command = temp.Script("claude", $"printf '%s' \"$CLAUDE_CONFIG_DIR\" > \"{temp.Combine("config-seen.txt")}\"");
        IPromptProvider provider = new PromptProvider(new ClaudeEngine(command, temp.Combine("claude-state")), temp.Combine("data"), new ListLogger(), TimeProvider.System);

        PromptTestResult result = await provider.Test();
        temp.Track(await provider.Create());

        Assert.False(result.IsAvailable);
        Assert.Equal(temp.Combine("claude-state"), await File.ReadAllTextAsync(temp.Combine("config-seen.txt")));
        Assert.Single(Directory.GetFiles(temp.Combine("data/Sessions")));
    }

    [Fact]
    public async Task AProviderOfCodexUsesTheCommandTheConfigFolderAndTheDataFolderItIsGiven()
    {
        using TempFolder temp = new();
        string command = temp.Script("codex", $"printf '%s' \"$CODEX_HOME\" > \"{temp.Combine("config-seen.txt")}\"");
        IPromptProvider provider = new PromptProvider(new CodexEngine(command, temp.Combine("codex-state")), temp.Combine("data"));

        PromptTestResult result = await provider.Test();
        temp.Track(await provider.Create());

        Assert.False(result.IsAvailable);
        Assert.Equal(temp.Combine("codex-state"), await File.ReadAllTextAsync(temp.Combine("config-seen.txt")));
        Assert.Single(Directory.GetFiles(temp.Combine("data/Sessions")));
    }

    [Fact]
    public async Task AProviderOfOpenCodeForALocalModelUsesTheCommandTheAddressAndTheContextSizeItIsGiven()
    {
        using TempFolder temp = new();
        string command = temp.Script("opencode", $"printf '%s' \"$OPENCODE_CONFIG_CONTENT\" > \"{temp.Combine("config-seen.txt")}\"");
        IPromptProvider provider = new PromptProvider(new OpenCodeEngine(command, OpenCodeModels.Ollama("gemma4:26b", "http://box:11434/v1", 8192)), temp.Combine("data"));

        await provider.Test();
        temp.Track(await provider.Create());

        JsonElement config = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("config-seen.txt"))).RootElement;
        JsonElement ollama = config.GetProperty("provider").GetProperty("ollama");
        Assert.Equal("http://box:11434/v1", ollama.GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Equal(8192, ollama.GetProperty("models").GetProperty("gemma4:26b").GetProperty("limit").GetProperty("context").GetInt32());
        Assert.Single(Directory.GetFiles(temp.Combine("data/Sessions")));
    }

    [Fact]
    public async Task AProviderOfOpenCodeForSeveralModelsUsesTheCommandTheAddressAndTheContextSizeItIsGiven()
    {
        using TempFolder temp = new();
        string command = temp.Script("opencode", $"printf '%s' \"$OPENCODE_CONFIG_CONTENT\" > \"{temp.Combine("config-seen.txt")}\"");
        OpenCodeModels models = OpenCodeModels.ByCapability([(PromptCapability.Fast, "small:1b"), (PromptCapability.Max, "anthropic/claude-sonnet")], "http://box:11434/v1", 4096);
        IPromptProvider provider = new PromptProvider(new OpenCodeEngine(command, models), temp.Combine("data"));

        await provider.Test();

        JsonElement config = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("config-seen.txt"))).RootElement;
        Assert.Equal("ollama/small:1b", config.GetProperty("model").GetString());
        JsonElement ollama = config.GetProperty("provider").GetProperty("ollama");
        Assert.Equal("http://box:11434/v1", ollama.GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Equal(4096, ollama.GetProperty("models").GetProperty("small:1b").GetProperty("limit").GetProperty("context").GetInt32());
    }

    [Fact]
    public async Task AProviderOfOpenCodeForModelsYouDescribeUsesTheModelsTheCommandAndTheDataFolder()
    {
        using TempFolder temp = new();
        string command = temp.Script("opencode", $"printf '%s' \"$OPENCODE_CONFIG_CONTENT\" > \"{temp.Combine("config-seen.txt")}\"");
        OpenCodeModels models = new()
        {
            Fast = "lmstudio/qwen3-coder",
            Providers = [new OpenCodeModelProvider { Id = "lmstudio", BaseUrl = "http://box:1234/v1", Models = [new OpenCodeModel { Id = "qwen3-coder" }] }]
        };
        IPromptProvider provider = new PromptProvider(new OpenCodeEngine(command, models), temp.Combine("data"));

        await provider.Test();
        temp.Track(await provider.Create());

        JsonElement config = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("config-seen.txt"))).RootElement;
        Assert.Equal("lmstudio/qwen3-coder", config.GetProperty("model").GetString());
        Assert.Equal("http://box:1234/v1", config.GetProperty("provider").GetProperty("lmstudio").GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Single(Directory.GetFiles(temp.Combine("data/Sessions")));
    }
}
