namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionOptionsJsonTests
{
    [Fact]
    public void Claude_KeepsItsKindItsValuesAndTheKeyButNotTheWorkspace()
    {
        PromptSessionOptions options = new ClaudeSessionOptions { ApiKey = "secret", WorkspacePath = "/project", Command = "my-claude", ConfigFolder = "/config" };

        string json = JsonSerializer.Serialize(options);
        PromptSessionOptions? back = JsonSerializer.Deserialize<PromptSessionOptions>(json);

        Assert.DoesNotContain("/project", json, StringComparison.Ordinal);
        Assert.Equal(new ClaudeSessionOptions { ApiKey = "secret", Command = "my-claude", ConfigFolder = "/config" }, back);
    }

    [Fact]
    public void Codex_KeepsItsKindItsValuesAndTheKey()
    {
        PromptSessionOptions options = new CodexSessionOptions { ApiKey = "secret", Command = "my-codex" };

        string json = JsonSerializer.Serialize(options);

        Assert.Equal(options, JsonSerializer.Deserialize<PromptSessionOptions>(json));
    }

    [Fact]
    public void Ollama_KeepsTheServerTheModelsAndTheContext()
    {
        PromptSessionOptions options = new OllamaSessionOptions { Server = new Uri("http://box:11434/v1"), Models = new PromptCapabilityModels { Fast = "small", Balanced = "gemma4:26b", Strong = "big", Max = "huge" }, Context = 16384, Command = "oc" };

        PromptSessionOptions? back = JsonSerializer.Deserialize<PromptSessionOptions>(JsonSerializer.Serialize(options));

        Assert.Equal(options, back);
    }

    [Fact]
    public void ThePolymorphicOptionsOfALinkComeBackAsTheSameKind()
    {
        PromptSessionLink link = new() { Id = Guid.NewGuid(), Provider = "Codex", WorkingFolder = "/w", Options = new CodexSessionOptions(), ImageGeneration = new OpenAiImageOptions { ApiKey = "secret" }, Capability = PromptCapability.Strong, Effort = PromptEffort.High, IsWebAllowed = true, Instructions = "Be brief." };

        string json = JsonSerializer.Serialize(link);
        PromptSessionLink? back = JsonSerializer.Deserialize<PromptSessionLink>(json);

        Assert.Equal(link, back);
    }
}
