namespace Markwardt.LiveAi.Tests.Unit.Models;

public sealed class LocalServerTests
{
    [Fact]
    public async Task List_GivesTheOllamaModelsThatCanCallToolsWithTheirFacts()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """
            { "models": [
                { "name": "gemma4:26b", "details": { "context_length": 262144 }, "capabilities": ["completion", "vision", "tools", "thinking"] },
                { "name": "nomic-embed-text:latest", "capabilities": ["embedding"] },
                { "name": "qwen3:8b", "capabilities": ["completion", "tools"] }
            ] }
            """);
        LocalServer local = new(server.Api);

        IReadOnlyList<PromptModel> models = await local.List();

        Assert.Equal([$"{local.ProviderId}/gemma4:26b", $"{local.ProviderId}/qwen3:8b"], models.Select(model => model.Id));
        Assert.Equal(("gemma4:26b", true, true, 262144), (models[0].Name, models[0].AcceptsImages, models[0].CallsTools, models[0].MaxContextLimit));
        Assert.Equal((false, 131072), (models[1].AcceptsImages, models[1].MaxContextLimit));
        Assert.All(models, model => Assert.Null(model.ContextLimit));
        Assert.DoesNotContain("/api/show", server.Requested);
    }

    [Fact]
    public async Task List_OffersTheContextWindowAsTheOnlySettingBecauseOpenCodeOffersNoReasoningForALocalModel()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """
            { "models": [
                { "name": "thinker", "details": { "context_length": 65536 }, "capabilities": ["tools", "thinking"] },
                { "name": "plain", "details": { "context_length": 65536 }, "capabilities": ["tools"] }
            ] }
            """);

        IReadOnlyList<PromptModel> models = await new LocalServer(server.Api).List();

        PromptModelSetting context = models[0].Settings!.Single(setting => setting.Id == "context");
        Assert.Equal((PromptModelSettingKind.Number, 1024, 65536, "4096"), (context.Kind, context.Minimum, context.Maximum, context.Default));
        Assert.Equal(["context"], models[0].Settings!.Select(setting => setting.Id));
        Assert.Equal(["context"], models[1].Settings!.Select(setting => setting.Id));
    }

    [Fact]
    public async Task List_GivesTheContextSizeOfAModelThatIsLoadedAndUsesItAsTheDefaultOfTheSetting()
    {
        using FakeModelServer server = new FakeModelServer()
            .Answer("/api/tags", """{ "models": [{ "name": "a", "details": { "context_length": 262144 }, "capabilities": ["tools"] }, { "name": "b", "capabilities": ["tools"] }] }""")
            .Answer("/api/ps", """{ "models": [{ "name": "a", "context_length": 16384 }, { "name": "other" }] }""");

        IReadOnlyList<PromptModel> models = await new LocalServer(server.Api).List();

        Assert.Equal((16384, null), (models[0].ContextLimit, models[1].ContextLimit));
        Assert.Equal("16384", models[0].Settings!.Single().Default);
        Assert.Equal("4096", models[1].Settings!.Single().Default);
    }

    [Fact]
    public async Task List_KeepsTheDefaultContextInsideARangeThatIsSmallerThanIt()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "tiny", "details": { "context_length": 2048 }, "capabilities": ["tools"] }] }""");

        PromptModelSetting context = (await new LocalServer(server.Api).List()).Single().Settings!.Single();

        Assert.Equal(("2048", 2048), (context.Default, context.Maximum));
        Assert.True(context.Accepts(context.Default));
    }

    [Fact]
    public async Task List_ReadsTheCapabilitiesOfAnOlderOllamaFromShow()
    {
        using FakeModelServer server = new FakeModelServer()
            .Answer("/api/tags", """{ "models": [{ "name": "a" }] }""")
            .Answer("/api/show", """{ "capabilities": ["embedding"] }""");

        Assert.Empty(await new LocalServer(server.Api).List());
        Assert.Contains("/api/show", server.Requested);
    }

    [Theory]
    [InlineData(404, "{}")]
    [InlineData(200, """{ "details": 1 }""")]
    public async Task List_KeepsAModelWhoseCapabilitiesCannotBeTold(int status, string shown)
    {
        using FakeModelServer server = new FakeModelServer()
            .Answer("/api/tags", """{ "models": [{ "name": "a" }] }""")
            .Answer("/api/show", shown, status);

        Assert.Single(await new LocalServer(server.Api).List());
    }

    [Fact]
    public async Task List_AsksAnyOtherServerForItsModelsAndGivesAllOfThem()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/v1/models", """{ "data": [{ "id": "qwen3-coder" }, { "id": "llama" }, { "object": "x" }] }""");
        LocalServer local = new(server.Api);

        IReadOnlyList<PromptModel> models = await local.List();

        Assert.Equal([$"{local.ProviderId}/qwen3-coder", $"{local.ProviderId}/llama"], models.Select(model => model.Id));
        Assert.Equal(["context"], models[0].Settings!.Select(setting => setting.Id));
        Assert.Equal((null, null), (models[0].AcceptsImages, models[0].CallsTools));
    }

    [Theory]
    [InlineData("http://127.0.0.1:{0}/v1/")]
    [InlineData("http://127.0.0.1:{0}")]
    public async Task List_FindsOllamaWhateverTheFormOfTheAddress(string form)
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "a", "capabilities": ["tools"] }] }""");

        Assert.Single(await new LocalServer(string.Format(form, new Uri(server.Root).Port)).List());
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", "ollama")]
    [InlineData("http://127.0.0.1:11434", "ollama")]
    [InlineData("http://localhost:1234/v1", "local-1234")]
    [InlineData("http://box:11434/v1", "local-box-11434")]
    [InlineData("not an address", "local")]
    public void TheProviderIdentifierNamesOllamaOnItsUsualPortAndOtherServersByTheirPort(string address, string expected)
        => Assert.Equal(expected, new LocalServer(address).ProviderId);

    [Fact]
    public void TheBaseUrlIsTheAddressWithoutATrailingSlash()
        => Assert.Equal("http://localhost:11434/v1", new LocalServer("http://localhost:11434/v1/").BaseUrl);

    [Fact]
    public async Task List_GivesNothingWhenTheServerIsNotRunning()
    {
        string api;
        using (FakeModelServer server = new())
        {
            api = server.Api;
        }

        Assert.Empty(await new LocalServer(api).List());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "models": 5 }""")]
    [InlineData("""{ "other": [] }""")]
    public async Task List_GivesNothingWhenTheAnswerCannotBeRead(string body)
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", body).Answer("/v1/models", body);

        Assert.Empty(await new LocalServer(server.Api).List());
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("not an address")]
    public async Task List_GivesNothingForAnAddressThatIsNotOne(string address)
        => Assert.Empty(await new LocalServer(address).List());

    [Fact]
    public async Task List_GivesNothingWhenTheServerDoesNotAnswerInTime()
    {
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();

        Assert.Empty(await new LocalServer($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/v1") { Timeout = TimeSpan.FromMilliseconds(300) }.List());
    }

    [Fact]
    public async Task List_UsesTheClientItIsGiven()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/v1/models", """{ "data": [{ "id": "m" }] }""");
        using HttpClient http = new();

        Assert.Single(await new LocalServer(server.Api, http).List());
    }

    [Fact]
    public async Task List_StopsWhenTheCallerCancels()
    {
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalServer($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/v1").List(cancellation.Token));
    }
}
