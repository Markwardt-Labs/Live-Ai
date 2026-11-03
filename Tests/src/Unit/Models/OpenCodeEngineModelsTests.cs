namespace Markwardt.LiveAi.Tests.Unit.Models;

[Collection("Processes")]
public sealed class OpenCodeEngineModelsTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly string configured = """
        {"data":[
          {"providerID":"opencode","modelID":"big-pickle","name":"Big Pickle","enabled":true,"capabilities":{"tools":true,"input":["text"]},"variants":[]},
          {"providerID":"anthropic","modelID":"claude-sonnet","name":"Claude Sonnet","enabled":true,"capabilities":{"tools":true,"input":["text","image"]},"limit":{"context":200000,"output":8000},"variants":[{"id":"low"},{"id":"medium"},{"id":"high"}]},
          {"providerID":"anthropic","modelID":"claude-haiku","enabled":false,"capabilities":{"tools":true,"input":["text"]}},
          {"providerID":"fake","modelID":"ours","enabled":true},
          {"providerID":"openai","modelID":"gpt","enabled":true,"capabilities":{"tools":false,"input":["text"]},"limit":{"context":0}}
        ]}
        """;

    public void Dispose()
        => temp.Dispose();

    private OpenCodeEngine Engine(string? models = null, IEnumerable<string>? servers = null, IEnumerable<OpenCodeModelProvider>? providers = null, string? command = null)
        => new(command ?? new FakeOpenCode(temp).Script(fork: models), servers ?? [], providers);

    [Fact]
    public async Task GetModels_GivesTheModelsOfOpenCodesOwnConfigurationWithoutItsBuiltInHostedOnesOrOurs()
    {
        OpenCodeModelProvider ours = new() { Id = "fake", BaseUrl = "http://localhost:9/v1", Models = [new OpenCodeModel { Id = "ours" }] };

        IReadOnlyList<PromptModel> models = await Engine(configured, providers: [ours]).GetModels();

        Assert.Equal(["fake/ours", "anthropic/claude-sonnet", "openai/gpt"], models.Select(model => model.Id));
    }

    [Fact]
    public async Task GetModels_ReadsTheFactsAndTheVariantsOfAConfiguredModel()
    {
        IReadOnlyList<PromptModel> models = await Engine(configured).GetModels();
        PromptModel sonnet = models.Single(model => model.Id == "anthropic/claude-sonnet");
        PromptModel gpt = models.Single(model => model.Id == "openai/gpt");

        Assert.Equal(("Claude Sonnet", true, true, 200000), (sonnet.Name, sonnet.AcceptsImages, sonnet.CallsTools, sonnet.ContextLimit));
        PromptModelSetting variant = Assert.Single(sonnet.Settings!);
        Assert.Equal(("variant", "medium"), (variant.Id, variant.Default));
        Assert.Equal(["low", "medium", "high"], variant.Choices);
        Assert.Equal((false, false, null), (gpt.CallsTools, gpt.AcceptsImages, gpt.ContextLimit));
        Assert.Empty(gpt.Settings!);
    }

    [Fact]
    public async Task GetModels_PutsTheLocalModelsFirstAndAsksOpenCodeOnlyForTheRest()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "gemma", "capabilities": ["tools"] }] }""");

        IReadOnlyList<PromptModel> models = await Engine(configured, [server.Api]).GetModels();

        Assert.StartsWith("local-", models[0].Id);
        Assert.EndsWith("/gemma", models[0].Id);
        Assert.Equal("anthropic/claude-sonnet", models[1].Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"data":5}""")]
    [InlineData("""{"data":[{"providerID":"x"}]}""")]
    public async Task GetModels_GivesNoOpenCodeModelsWhenItsAnswerCannotBeRead(string answer)
        => Assert.Empty(await Engine(answer).GetModels());

    [Fact]
    public async Task GetModels_GivesNoneWhenOpenCodeIsNotThereAndNothingElseIsConfigured()
        => Assert.Empty(await Engine(command: temp.Combine("not-installed")).GetModels());

    [Fact]
    public async Task CreateTestStart_PrefersALocalModelOverOpenCodesOwn()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "gemma", "capabilities": ["tools", "thinking"] }] }""");
        OpenCodeEngine engine = Engine(configured, [server.Api]);

        ProcessStartInfo start = await engine.CreateTestStart(null);

        JsonElement config = JsonDocument.Parse(start.Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement;
        Assert.EndsWith("/gemma", config.GetProperty("model").GetString());
    }

    [Fact]
    public async Task CreateTestStart_FallsBackToTheFirstModelOfOpenCodesOwnConfiguration()
    {
        ProcessStartInfo start = await Engine(configured).CreateTestStart(null);

        Assert.Equal("anthropic/claude-sonnet", JsonDocument.Parse(start.Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task CreateTestStart_WithNoModelAtAll_LeavesTheChoiceToOpenCode()
    {
        ProcessStartInfo start = await Engine(command: temp.Combine("not-installed")).CreateTestStart(null);

        Assert.False(JsonDocument.Parse(start.Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement.TryGetProperty("model", out _));
    }
}
