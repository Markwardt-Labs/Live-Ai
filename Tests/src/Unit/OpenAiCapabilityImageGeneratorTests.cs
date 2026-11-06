namespace Markwardt.LiveAi.Tests.Unit;

public sealed class OpenAiCapabilityImageGeneratorTests
{
    private readonly List<HttpRequestMessage> sent = [];

    private readonly List<string> models = [];

    private Func<HttpRequestMessage, HttpResponseMessage> list;

    public OpenAiCapabilityImageGeneratorTests()
        => list = _ => Json("""{"data":[]}""");

    private OpenAiCapabilityImageGenerator Generator(PromptCapability capability)
        => new("sk-test", capability, new Answering(request =>
        {
            sent.Add(request);

            if (request.Method == HttpMethod.Get)
            {
                return list(request);
            }

            models.Add(JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("model").GetString()!);
            return Json($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(FakeImageGenerator.Png)}}"}]}""");
        }));

    private HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void TheGenerator_OffersTransparencyAndNeedsNoAdvice()
    {
        IPromptImageGenerator generator = Generator(PromptCapability.Balanced);

        Assert.Equal((PromptImageFeatures.Transparency, null), (generator.Features, generator.PromptGuide));
    }

    [Theory]
    [InlineData(PromptCapability.Fast, "gpt-image-1-mini")]
    [InlineData(PromptCapability.Balanced, "gpt-image-2.6-flare")]
    [InlineData(PromptCapability.Strong, "gpt-image-2.6-sunburst")]
    [InlineData(PromptCapability.Max, "gpt-image-2.6-sunburst")]
    public async Task Generate_WhenTheModelsCannotBeListed_UsesTheModelKnownForTheCapability(PromptCapability capability, string expected)
    {
        list = _ => Json("""{"error":{"message":"nope"}}""", HttpStatusCode.InternalServerError);

        await Generator(capability).Generate(new PromptImageRequest { Prompt = "x" });

        Assert.Equal([expected], models);
    }

    [Theory]
    [InlineData(PromptCapability.Fast, "gpt-image-3-mini")]
    [InlineData(PromptCapability.Balanced, "gpt-image-3-flare")]
    [InlineData(PromptCapability.Max, "gpt-image-3-sunburst")]
    public async Task Generate_TakesTheNewestListedModelOfTheKindTheCapabilityMeans(PromptCapability capability, string expected)
    {
        list = _ => Json("""{"data":[{"id":"gpt-image-2.6-flare","created":100},{"id":"gpt-image-3-flare","created":300},{"id":"gpt-image-3-mini","created":300},{"id":"gpt-image-1-mini","created":50},{"id":"gpt-image-3-sunburst","created":300},{"id":"gpt-image-2","created":900}]}""");

        await Generator(capability).Generate(new PromptImageRequest { Prompt = "x" });

        Assert.Equal([expected], models);
    }

    [Fact]
    public async Task Generate_ListsTheModelsOnceAndKeepsTheChoiceForEveryImage()
    {
        list = _ => Json("""{"data":[{"id":"gpt-image-2.6-flare","created":100}]}""");
        OpenAiCapabilityImageGenerator generator = Generator(PromptCapability.Balanced);

        await generator.Generate(new PromptImageRequest { Prompt = "one" });
        await generator.Generate(new PromptImageRequest { Prompt = "two" });

        Assert.Single(sent, request => request.Method == HttpMethod.Get);
        Assert.Equal(["gpt-image-2.6-flare", "gpt-image-2.6-flare"], models);
    }

    [Fact]
    public async Task Generate_SendsTheListingAndTheImagesTheKey()
    {
        await Generator(PromptCapability.Balanced).Generate(new PromptImageRequest { Prompt = "x" });

        Assert.All(sent, request => Assert.Equal("Bearer sk-test", request.Headers.Authorization!.ToString()));
        Assert.Equal(["https://api.openai.com/v1/models", "https://api.openai.com/v1/images/generations"], sent.Select(request => request.RequestUri!.ToString()));
    }
}
