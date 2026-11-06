namespace Markwardt.LiveAi.Tests.Unit;

public sealed class StableDiffusionImageCatalogTests
{
    private readonly List<HttpRequestMessage> sent = [];

    private Func<HttpRequestMessage, HttpResponseMessage> answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private IStableDiffusionImageCatalog Catalog(Uri? baseUrl = null)
        => new StableDiffusionImageCatalog(baseUrl ?? new Uri("http://localhost:7860"), new HttpClient(new Answering(request =>
        {
            sent.Add(request);
            return answer(request);
        })));

    private HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("http://localhost:7860", "http://localhost:7860/sdapi/v1/sd-models")]
    [InlineData("http://gpu-box:9000/", "http://gpu-box:9000/sdapi/v1/sd-models")]
    public async Task List_AsksTheModelsEndpointOfTheServer(string baseUrl, string expected)
    {
        answer = _ => Json("[]");

        await Catalog(new Uri(baseUrl)).List();

        HttpRequestMessage request = Assert.Single(sent);
        Assert.Equal((HttpMethod.Get, expected), (request.Method, request.RequestUri!.ToString()));
    }

    [Fact]
    public async Task List_GivesTheTitleAsTheIdAndTheModelNameAsTheName()
    {
        answer = _ => Json("""
            [
              {"title":"v1-5-pruned.safetensors [6ce0161689]","model_name":"v1-5-pruned","hash":"6ce0161689","filename":"/models/v1-5-pruned.safetensors"},
              {"title":"animePastel.safetensors [aa11bb22cc]","model_name":"animePastel","hash":"aa11bb22cc"}
            ]
            """);

        IReadOnlyList<PromptImageModel> models = await Catalog().List();

        Assert.Equal(
            [("animePastel.safetensors [aa11bb22cc]", "animePastel"), ("v1-5-pruned.safetensors [6ce0161689]", "v1-5-pruned")],
            models.Select(model => (model.Id, model.Name)));
    }

    [Fact]
    public async Task List_ReadsTheAnswerOfARealStableDiffusionCppServer()
    {
        answer = _ => Json("""[{"config":null,"filename":"sd_turbo.safetensors","hash":"8888888888","model_name":"sd_turbo","sha256":"8888888888888888888888888888888888888888888888888888888888888888","title":"sd_turbo"}]""");

        PromptImageModel model = Assert.Single(await Catalog().List());

        Assert.Equal(("sd_turbo", null), (model.Id, model.Name));
    }

    [Fact]
    public async Task List_GivesNoNameWhenItWouldRepeatTheId()
    {
        answer = _ => Json("""[{"title":"sd_turbo","model_name":"sd_turbo","filename":"sd_turbo.safetensors"}]""");

        PromptImageModel model = Assert.Single(await Catalog().List());

        Assert.Equal(("sd_turbo", null), (model.Id, model.Name));
    }

    [Fact]
    public async Task List_FallsBackToTheModelNameAndThenTheFilename()
    {
        answer = _ => Json("""[{"model_name":"named"},{"filename":"/models/file.safetensors"},{"hash":"nothing else"}]""");

        IReadOnlyList<PromptImageModel> models = await Catalog().List();

        Assert.Equal(["/models/file.safetensors", "named"], models.Select(model => model.Id));
    }

    [Fact]
    public async Task List_GivesEveryModelTheFeaturesOfTheGenerator()
    {
        answer = _ => Json("""[{"title":"a"},{"title":"b"}]""");

        IReadOnlyList<PromptImageModel> models = await Catalog().List();

        Assert.All(models, model => Assert.Equal(PromptImageFeatures.NegativePrompt | PromptImageFeatures.Seed, model.Features));
    }

    [Theory]
    [InlineData("""[null,1,"x",{"title":5},{"title":""},{}]""")]
    [InlineData("[]")]
    public async Task List_SkipsEntriesWithoutAUsableName(string body)
    {
        answer = _ => Json(body);

        Assert.Empty(await Catalog().List());
    }

    [Theory]
    [InlineData("""{"detail":"Not Found"}""", HttpStatusCode.NotFound, "Not Found")]
    [InlineData("""{"error":"boom"}""", HttpStatusCode.InternalServerError, "boom")]
    [InlineData("not json", HttpStatusCode.BadGateway, "502")]
    public async Task List_ReportsARefusal(string body, HttpStatusCode status, string expected)
    {
        answer = _ => Json(body, status);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().List());

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"models":[]}""")]
    [InlineData("not json")]
    [InlineData("12")]
    public async Task List_ReportsAnAnswerThatIsNotAList(string body)
    {
        answer = _ => Json(body);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().List());
    }

    [Fact]
    public async Task List_ReportsAServerThatIsNotRunningWithAdvice()
    {
        answer = _ => throw new HttpRequestException("connection refused");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().List());

        Assert.Contains("connection refused", exception.Message, StringComparison.Ordinal);
        Assert.Contains("API turned on", exception.Message, StringComparison.Ordinal);
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IPromptImageGenerator.StableDiffusionModels(null, cancellation.Token));
    }
}
