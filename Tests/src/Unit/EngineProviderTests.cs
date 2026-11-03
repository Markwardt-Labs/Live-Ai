namespace Markwardt.LiveAi.Tests.Unit;

[Collection("Processes")]
public sealed class EngineProviderTests
{
    [Fact]
    public async Task AProviderOfClaudeUsesTheCommandTheConfigFolderAndTheDataFolderItIsGiven()
    {
        using TempFolder temp = new();
        string command = temp.Script("claude", $"printf '%s' \"$CLAUDE_CONFIG_DIR\" > \"{temp.Combine("config-seen.txt")}\"");
        IPromptProvider provider = new PromptProvider(new ClaudeEngine(command, temp.Combine("claude-state")), temp.Combine("data"), new ListLogger(), TimeProvider.System);

        PromptTestResult result = await provider.Test();
        temp.Track(await provider.Create(TestModels.Options));

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
        temp.Track(await provider.Create(TestModels.Options));

        Assert.False(result.IsAvailable);
        Assert.Equal(temp.Combine("codex-state"), await File.ReadAllTextAsync(temp.Combine("config-seen.txt")));
        Assert.Single(Directory.GetFiles(temp.Combine("data/Sessions")));
    }

    [Fact]
    public async Task AProviderOfOpenCodeOverALocalServerListsItsModelsAndTestsOnTheFirst()
    {
        using TempFolder temp = new();
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "gemma4:26b", "capabilities": ["tools", "thinking"] }, { "name": "embed", "capabilities": ["embedding"] }] }""");
        string command = temp.Script("opencode", $"printf '%s' \"$OPENCODE_CONFIG_CONTENT\" > \"{temp.Combine("config-seen.txt")}\"");
        IPromptProvider provider = new PromptProvider(new OpenCodeEngine(command, [server.Api]), temp.Combine("data"));

        PromptModel model = Assert.Single(await provider.GetModels());
        await provider.Test();

        JsonElement config = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("config-seen.txt"))).RootElement;
        Assert.Equal(model.Id, config.GetProperty("model").GetString());
        Assert.Equal(server.Api, config.GetProperty("provider").GetProperty(model.Id.Split('/')[0]).GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Equal(["context"], model.Settings!.Select(setting => setting.Id));
    }

    [Fact]
    public async Task AProviderOfOpenCodeForModelsYouDescribeUsesTheModelsTheCommandAndTheDataFolder()
    {
        using TempFolder temp = new();
        string command = temp.Script("opencode", $"printf '%s' \"$OPENCODE_CONFIG_CONTENT\" > \"{temp.Combine("config-seen.txt")}\"");
        OpenCodeModelProvider described = new() { Id = "lmstudio", BaseUrl = "http://box:1234/v1", Models = [new OpenCodeModel { Id = "qwen3-coder" }] };
        IPromptProvider provider = new PromptProvider(new OpenCodeEngine(command, [], [described]), temp.Combine("data"));

        await provider.Test();
        temp.Track(await provider.Create(new PromptSessionOptions { Model = new PromptModel { Id = "lmstudio/qwen3-coder" } }));

        JsonElement config = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("config-seen.txt"))).RootElement;
        Assert.Equal("lmstudio/qwen3-coder", config.GetProperty("model").GetString());
        Assert.Equal("http://box:1234/v1", config.GetProperty("provider").GetProperty("lmstudio").GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Single(Directory.GetFiles(temp.Combine("data/Sessions")));
    }
}
