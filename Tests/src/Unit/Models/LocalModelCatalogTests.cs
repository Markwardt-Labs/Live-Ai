namespace Markwardt.LiveAi.Tests.Unit.Models;

public sealed class LocalModelCatalogTests
{
    [Fact]
    public async Task List_GivesTheOllamaModelsThatCanCallTools()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """
            { "models": [
                { "name": "gemma4:26b", "capabilities": ["completion", "tools", "thinking"] },
                { "name": "nomic-embed-text:latest", "capabilities": ["embedding"] },
                { "name": "qwen3:8b", "capabilities": ["completion", "tools"] }
            ] }
            """);

        IReadOnlyList<PromptModel> models = await new LocalModelCatalog(server.Api).List();

        Assert.Equal(["gemma4:26b", "qwen3:8b"], models.Select(model => model.Id));
        Assert.All(models, model => Assert.Null(model.ContextLimit));
        Assert.DoesNotContain("/api/show", server.Requested);
    }

    [Fact]
    public async Task List_GivesTheContextSizeOfTheModelsThatAreLoaded()
    {
        using FakeModelServer server = new FakeModelServer()
            .Answer("/api/tags", """{ "models": [{ "name": "a", "capabilities": ["tools"] }, { "name": "b", "capabilities": ["tools"] }] }""")
            .Answer("/api/ps", """{ "models": [{ "name": "b", "context_length": 16384 }, { "name": "other" }] }""");

        IReadOnlyList<PromptModel> models = await new LocalModelCatalog(server.Api).List();

        Assert.Equal((null, 16384), (models[0].ContextLimit, models[1].ContextLimit));
    }

    [Fact]
    public async Task List_ReadsTheCapabilitiesOfAnOlderOllamaFromShow()
    {
        using FakeModelServer server = new FakeModelServer()
            .Answer("/api/tags", """{ "models": [{ "name": "a" }] }""")
            .Answer("/api/show", """{ "capabilities": ["embedding"] }""");

        Assert.Empty(await new LocalModelCatalog(server.Api).List());
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

        Assert.Equal(["a"], (await new LocalModelCatalog(server.Api).List()).Select(model => model.Id));
    }

    [Fact]
    public async Task List_AsksAnyOtherServerForItsModelsAndGivesAllOfThem()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/v1/models", """{ "data": [{ "id": "qwen3-coder" }, { "id": "llama" }, { "object": "x" }] }""");

        Assert.Equal(["qwen3-coder", "llama"], (await new LocalModelCatalog(server.Api).List()).Select(model => model.Id));
    }

    [Theory]
    [InlineData("http://127.0.0.1:{0}/v1/")]
    [InlineData("http://127.0.0.1:{0}")]
    public async Task List_FindsOllamaWhateverTheFormOfTheAddress(string form)
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "a", "capabilities": ["tools"] }] }""");

        Assert.Equal(["a"], (await new LocalModelCatalog(string.Format(form, new Uri(server.Root).Port)).List()).Select(model => model.Id));
    }

    [Fact]
    public async Task List_GivesNothingWhenTheServerIsNotRunning()
    {
        string api;
        using (FakeModelServer server = new())
        {
            api = server.Api;
        }

        Assert.Empty(await new LocalModelCatalog(api).List());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "models": 5 }""")]
    [InlineData("""{ "other": [] }""")]
    public async Task List_GivesNothingWhenTheAnswerCannotBeRead(string body)
    {
        using FakeModelServer server = new FakeModelServer().Answer("/api/tags", body).Answer("/v1/models", body);

        Assert.Empty(await new LocalModelCatalog(server.Api).List());
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("not an address")]
    public async Task List_GivesNothingForAnAddressThatIsNotOne(string address)
        => Assert.Empty(await new LocalModelCatalog(address).List());

    [Fact]
    public async Task List_GivesNothingWhenTheServerDoesNotAnswerInTime()
    {
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();

        Assert.Empty(await new LocalModelCatalog($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/v1") { Timeout = TimeSpan.FromMilliseconds(300) }.List());
    }

    [Fact]
    public async Task List_UsesTheClientItIsGiven()
    {
        using FakeModelServer server = new FakeModelServer().Answer("/v1/models", """{ "data": [{ "id": "m" }] }""");
        using HttpClient http = new();

        Assert.Single(await new LocalModelCatalog(server.Api, http).List());
    }

    [Fact]
    public async Task List_StopsWhenTheCallerCancels()
    {
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalModelCatalog($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/v1").List(cancellation.Token));
    }
}
