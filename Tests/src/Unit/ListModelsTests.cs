namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ListModelsTests
{
    [Fact]
    public async Task Ollama_GivesTheNamesOfTheModelsThatCanCallTools()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """
            { "models": [
                { "name": "gemma4:26b", "capabilities": ["completion", "vision", "tools"] },
                { "name": "nomic-embed-text:latest", "capabilities": ["embedding"] },
                { "name": "qwen3:8b", "capabilities": ["completion", "tools"] }
            ] }
            """);

        IReadOnlyList<string> models = await OllamaSessionOptions.ListModels(new Uri(server.Api));

        Assert.Equal(["gemma4:26b", "qwen3:8b"], models);
    }

    [Fact]
    public async Task Ollama_GivesNoneWhenTheServerCannotBeReached()
        => Assert.Empty(await OllamaSessionOptions.ListModels(new Uri("http://127.0.0.1:1/v1")));

    [Fact]
    public async Task Ollama_StopsWhenTheCallerCancels()
    {
        using FakeModelServer server = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OllamaSessionOptions.ListModels(new Uri(server.Api), cancellation.Token));
    }

    [Fact]
    public async Task Ollama_NamesAreAcceptedByTheModelMapping()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "gemma4:26b", "capabilities": ["tools"] }] }""");

        string name = Assert.Single(await OllamaSessionOptions.ListModels(new Uri(server.Api)));

        PromptCapabilityModels models = PromptCapabilityModels.All(name);
        Assert.Equal("gemma4:26b", models.For(PromptCapability.Max));
    }

    [Fact]
    public async Task StableDiffusion_GivesTheTitlesOfTheCheckpoints()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/sdapi/v1/sd-models", """
            [
              {"title":"v1-5-pruned.safetensors [6ce0161689]","model_name":"v1-5-pruned","hash":"6ce0161689"},
              {"title":"animePastel.safetensors [aa11bb22cc]","model_name":"animePastel","hash":"aa11bb22cc"}
            ]
            """);

        IReadOnlyList<string> models = await StableDiffusionImageOptions.ListModels(new Uri(server.Root));

        Assert.Equal(["animePastel.safetensors [aa11bb22cc]", "v1-5-pruned.safetensors [6ce0161689]"], models.Order());
    }

    [Fact]
    public async Task StableDiffusion_ExplainsWhenTheServerCannotBeAsked()
    {
        using FakeModelServer server = new();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StableDiffusionImageOptions.ListModels(new Uri(server.Root)));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public async Task StableDiffusion_TitlesAreAcceptedByTheCheckpointMapping()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/sdapi/v1/sd-models", """[{"title":"sd_turbo","model_name":"sd_turbo"}]""");

        string title = Assert.Single(await StableDiffusionImageOptions.ListModels(new Uri(server.Root)));

        new StableDiffusionImageOptions { Models = PromptCapabilityModels.All(title) }.Validate();
    }
}
