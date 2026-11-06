namespace Markwardt.LiveAi.Tests.Unit;

public sealed class OpenAiImageGeneratorTests
{
    private readonly List<HttpRequestMessage> sent = [];

    private readonly List<JsonElement> bodies = [];

    private Func<HttpRequestMessage, HttpResponseMessage> answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private IPromptImageGenerator Generator(string key = "sk-test", string model = "gpt-image-2.6-flare", Uri? baseUrl = null)
        => new OpenAiImageGenerator(key, model, baseUrl ?? new Uri("https://api.openai.com/v1"), new HttpClient(new Answering(request =>
        {
            sent.Add(request);
            bodies.Add(JsonDocument.Parse(request.Content is null ? "{}" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone());
            return answer(request);
        })));

    private HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private string Encoded(byte[]? bytes = null)
        => Convert.ToBase64String(bytes ?? FakeImageGenerator.Png);

    [Fact]
    public async Task Generate_PostsTheRequestToTheImageEndpointWithTheKey()
    {
        answer = _ => Json($$"""{"data":[{"b64_json":"{{Encoded()}}"}]}""");

        IReadOnlyList<PromptImageResult> images = await Generator().Generate(new PromptImageRequest { Prompt = "a red fox", Count = 2, Quality = PromptImageQuality.High });

        HttpRequestMessage request = Assert.Single(sent);
        Assert.Equal(("https://api.openai.com/v1/images/generations", HttpMethod.Post, "Bearer sk-test"), (request.RequestUri!.ToString(), request.Method, request.Headers.Authorization!.ToString()));
        JsonElement body = Assert.Single(bodies);
        Assert.Equal(("gpt-image-2.6-flare", "a red fox", 2, "high", "auto"), (body.GetProperty("model").GetString(), body.GetProperty("prompt").GetString(), body.GetProperty("n").GetInt32(), body.GetProperty("quality").GetString(), body.GetProperty("size").GetString()));
        Assert.False(body.TryGetProperty("background", out _));
        Assert.False(body.TryGetProperty("response_format", out _));
        Assert.Equal(FakeImageGenerator.Png, Assert.Single(images).Content.ToArray());
    }

    [Theory]
    [InlineData(PromptImageQuality.Draft, "low")]
    [InlineData(PromptImageQuality.Standard, "medium")]
    [InlineData(PromptImageQuality.High, "high")]
    public async Task Generate_TurnsTheQualityIntoALevelOfTheService(PromptImageQuality quality, string level)
    {
        answer = _ => Json($$"""{"data":[{"b64_json":"{{Encoded()}}"}]}""");

        await Generator().Generate(new PromptImageRequest { Prompt = "x", Quality = quality });

        Assert.Equal(level, bodies.Single().GetProperty("quality").GetString());
    }

    [Theory]
    [InlineData(1024, 1024, "1024x1024")]
    [InlineData(800, 600, "1536x1024")]
    [InlineData(600, 900, "1024x1536")]
    [InlineData(1000, 900, "1024x1024")]
    public async Task Generate_SendsTheSizeOfTheThreeThatIsNearestTheShapeAsked(int width, int height, string size)
    {
        answer = _ => Json($$"""{"data":[{"b64_json":"{{Encoded()}}"}]}""");

        await Generator().Generate(new PromptImageRequest { Prompt = "x", Width = width, Height = height });

        Assert.Equal(size, bodies.Single().GetProperty("size").GetString());
    }

    [Fact]
    public async Task Generate_ForATransparentBackgroundAsksForAPngWithOne()
    {
        answer = _ => Json($$"""{"data":[{"b64_json":"{{Encoded()}}"}]}""");

        await Generator(model: "gpt-image-1.5").Generate(new PromptImageRequest { Prompt = "an icon", Transparent = true });

        JsonElement body = bodies.Single();
        Assert.Equal(("transparent", "png"), (body.GetProperty("background").GetString(), body.GetProperty("output_format").GetString()));
    }

    [Fact]
    public async Task Generate_UsesTheAddressItIsGivenAndSendsTheKeyThere()
    {
        answer = _ => Json($$"""{"data":[{"b64_json":"{{Encoded()}}"}]}""");

        await Generator("sk-local", "local", new Uri("http://localhost:8080/v1/")).Generate(new PromptImageRequest { Prompt = "x" });

        Assert.Equal("http://localhost:8080/v1/images/generations", sent.Single().RequestUri!.ToString());
        Assert.Equal("sk-local", sent.Single().Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Generate_ReportsTheMessageOfAnErrorTheServiceGives()
    {
        answer = _ => Json("""{"error":{"message":"Your request was rejected by the safety system.","type":"invalid_request_error"}}""", HttpStatusCode.BadRequest);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.Equal("Your request was rejected by the safety system.", exception.Message);
    }

    [Fact]
    public async Task Generate_LeavesTheKeyOutOfAnErrorThatRepeatsIt()
    {
        answer = _ => Json("""{"error":{"message":"Incorrect API key provided: sk-secret-123."}}""", HttpStatusCode.Unauthorized);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator("sk-secret-123").Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.DoesNotContain("sk-secret-123", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Generate_ReportsTheStatusWhenTheErrorHasNoMessage(string body)
    {
        answer = _ => Json(body, HttpStatusCode.InternalServerError);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"data":[{"revised_prompt":"x"}]}""")]
    [InlineData("""{"data":[{"url":"https://images.example/a.png"}]}""")]
    public async Task Generate_ReportsAnAnswerWithoutAnImage(string body)
    {
        answer = _ => Json(body);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.Contains("without any image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_ReportsAServiceThatCannotBeReached()
    {
        answer = _ => throw new HttpRequestException("no route");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.Contains("no route", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_StopsWhenTheCallerCancels()
    {
        answer = _ => throw new TaskCanceledException();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }, cancellation.Token));
    }

    [Fact]
    public void TheFactoryMakesAGeneratorThatNeedsNoAdvice()
    {
        IPromptImageGenerator generator = IPromptImageGenerator.OpenAi("key");

        Assert.Null(generator.PromptGuide);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TheFactoryRefusesAnEmptyKey(string key)
        => Assert.Throws<ArgumentException>(() => IPromptImageGenerator.OpenAi(key));

    [Fact]
    public void TheFactoryUsesTheFlareModelUnlessAnotherIsGiven()
    {
        IPromptImageGenerator generator = IPromptImageGenerator.OpenAi("key");
        IPromptImageGenerator other = IPromptImageGenerator.OpenAi("key", "gpt-image-2");

        Assert.Equal((PromptImageFeatures.Transparency, PromptImageFeatures.None), (generator.Features, other.Features));
    }

    [Theory]
    [InlineData("gpt-image-2", PromptImageFeatures.None)]
    [InlineData("GPT-Image-2-2026-04-21", PromptImageFeatures.None)]
    [InlineData("gpt-image-2.5-flare", PromptImageFeatures.Transparency)]
    [InlineData("gpt-image-2.6-sunburst", PromptImageFeatures.Transparency)]
    [InlineData("gpt-image-1.5", PromptImageFeatures.Transparency)]
    [InlineData("gpt-image-1-mini", PromptImageFeatures.Transparency)]
    [InlineData("some-other-model", PromptImageFeatures.Transparency)]
    public void Features_OfferTransparencyExceptWhereTheApiRefusesIt(string model, PromptImageFeatures expected)
        => Assert.Equal(expected, Generator(model: model).Features);
}
