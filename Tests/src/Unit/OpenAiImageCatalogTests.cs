namespace Markwardt.LiveAi.Tests.Unit;

public sealed class OpenAiImageCatalogTests
{
    private readonly List<HttpRequestMessage> sent = [];

    private Func<HttpRequestMessage, HttpResponseMessage> answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private IOpenAiImageCatalog Catalog(string key = "sk-test", Uri? baseUrl = null)
        => new OpenAiImageCatalog(key, baseUrl ?? new Uri("https://api.openai.com/v1"), new HttpClient(new Answering(request =>
        {
            sent.Add(request);
            return answer(request);
        })));

    private HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task List_AsksTheModelsEndpointWithTheKey()
    {
        answer = _ => Json("""{"data":[]}""");

        await Catalog().List();

        HttpRequestMessage request = Assert.Single(sent);
        Assert.Equal((HttpMethod.Get, "https://api.openai.com/v1/models", "Bearer", "sk-test"), (request.Method, request.RequestUri!.ToString(), request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
    }

    [Fact]
    public async Task List_UsesTheAddressItIsGivenAndSendsTheKeyThere()
    {
        answer = _ => Json("""{"data":[]}""");

        await Catalog("sk-local", new Uri("http://localhost:9000/v1/")).List();

        HttpRequestMessage request = Assert.Single(sent);
        Assert.Equal(("http://localhost:9000/v1/models", "sk-local"), (request.RequestUri!.ToString(), request.Headers.Authorization!.Parameter));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TheFactoryRefusesAnEmptyKey(string key)
        => await Assert.ThrowsAsync<ArgumentException>(() => IPromptImageGenerator.OpenAiModels(key));

    [Fact]
    public async Task List_KeepsOnlyTheImageModelsInTheOrderOfTheirNames()
    {
        answer = _ => Json("""
            {"data":[
              {"id":"gpt-5","created":1},
              {"id":"gpt-image-2.6-flare","created":1790000000},
              {"id":"dall-e-3","created":2},
              {"id":"chatgpt-image-latest","created":1780000000},
              {"id":"gpt-image-1-mini"},
              {"id":"text-embedding-3-small","created":3}
            ]}
            """);

        IReadOnlyList<PromptImageModel> models = await Catalog().List();

        Assert.Equal(["chatgpt-image-latest", "gpt-image-1-mini", "gpt-image-2.6-flare"], models.Select(model => model.Id));
    }

    [Fact]
    public async Task List_ReadsWhenAModelWasPublishedIfTheServiceSaysSo()
    {
        answer = _ => Json("""{"data":[{"id":"gpt-image-1.5","created":1790000000},{"id":"gpt-image-1-mini"}]}""");

        IReadOnlyList<PromptImageModel> models = await Catalog().List();

        Assert.Equal([null, DateTimeOffset.FromUnixTimeSeconds(1790000000)], models.Select(model => model.Created));
    }

    [Fact]
    public async Task List_GivesEachModelTheFeaturesItsGeneratorHas()
    {
        answer = _ => Json("""{"data":[{"id":"gpt-image-2"},{"id":"gpt-image-2-2026-04-21"},{"id":"gpt-image-2.5-flare"},{"id":"gpt-image-1.5"}]}""");

        IReadOnlyList<PromptImageModel> models = await Catalog().List();

        Assert.Equal(
            [("gpt-image-1.5", PromptImageFeatures.Transparency), ("gpt-image-2", PromptImageFeatures.None), ("gpt-image-2-2026-04-21", PromptImageFeatures.None), ("gpt-image-2.5-flare", PromptImageFeatures.Transparency)],
            models.Select(model => (model.Id, model.Features)));
    }

    [Theory]
    [InlineData("""{"data":[null,1,"x",{"name":"gpt-image-1"},{"id":5},{"id":""}]}""")]
    [InlineData("""{"data":[]}""")]
    public async Task List_SkipsEntriesWithoutAUsableName(string body)
    {
        answer = _ => Json(body);

        Assert.Empty(await Catalog().List());
    }

    [Fact]
    public async Task List_ReportsTheMessageOfARefusal()
    {
        answer = _ => Json("""{"error":{"message":"Incorrect API key provided."}}""", HttpStatusCode.Unauthorized);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().List());

        Assert.Equal("Incorrect API key provided.", exception.Message);
    }

    [Fact]
    public async Task List_LeavesTheKeyOutOfAMessageThatRepeatsIt()
    {
        answer = _ => Json("""{"error":{"message":"Incorrect API key provided: sk-secret-123. Check sk-secret-123."}}""", HttpStatusCode.Unauthorized);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog("sk-secret-123").List());

        Assert.Equal("Incorrect API key provided: <key>. Check <key>.", exception.Message);
    }

    [Fact]
    public async Task List_ReportsTheStatusOfARefusalWithoutAMessage()
    {
        answer = _ => Json("not json", HttpStatusCode.BadGateway);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().List());

        Assert.Contains("502", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"data":"x"}""")]
    [InlineData("not json")]
    public async Task List_ReportsAnAnswerThatIsNotAList(string body)
    {
        answer = _ => Json(body);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().List());
    }

    [Fact]
    public async Task List_ReportsAServiceThatCannotBeReached()
    {
        answer = _ => throw new HttpRequestException("no route");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().List());

        Assert.Contains("no route", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_StopsWhenTheCallerCancels()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog().List(cancellation.Token));
    }

    [Fact]
    public async Task TheFactoryListsWithoutTouchingTheNetworkWhenAlreadyCancelled()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IPromptImageGenerator.OpenAiModels("sk-test", null, cancellation.Token));
    }

    [Theory]
    [InlineData("GPT-IMAGE-2", PromptImageFeatures.None)]
    [InlineData("gpt-image-20", PromptImageFeatures.Transparency)]
    public void GetFeatures_MatchesTheNameOfTheModelThatRefusesTransparency(string model, PromptImageFeatures expected)
        => Assert.Equal(expected, Catalog().GetFeatures(model));
}
