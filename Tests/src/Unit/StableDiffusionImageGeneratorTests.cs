namespace Markwardt.LiveAi.Tests.Unit;

public sealed class StableDiffusionImageGeneratorTests
{
    private readonly List<HttpRequestMessage> sent = [];

    private readonly List<JsonElement> bodies = [];

    private Func<HttpRequestMessage, HttpResponseMessage> answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private IPromptImageGenerator Generator(PromptCapabilityModels? models = null, int width = 1024, int height = 1024, string? guide = null)
        => new StableDiffusionImageGenerator(new Uri("http://localhost:7860"), models ?? PromptCapabilityModels.All("default.safetensors"), width, height, [6, 20, 40], 3.5, guide, new HttpClient(new Answering(request =>
        {
            sent.Add(request);
            bodies.Add(JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone());
            return answer(request);
        })));

    private HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private string Answer(int images = 1, string? seeds = "[11,12]")
        => $$"""{"images":[{{string.Join(',', Enumerable.Range(0, images).Select(_ => $"\"{Convert.ToBase64String(FakeImageGenerator.Png)}\""))}}],"info":{{(seeds is null ? "\"\"" : JsonSerializer.Serialize($$"""{"all_seeds":{{seeds}}}"""))}}}""";

    [Fact]
    public async Task Generate_PostsTheRequestToTheTxt2ImgEndpointAndReturnsTheImagesWithTheirSeeds()
    {
        answer = _ => Json(Answer(2));

        IReadOnlyList<PromptImageResult> images = await Generator().Generate(new PromptImageRequest { Prompt = "a red fox", Count = 2, NegativePrompt = "blurry", Seed = 5, Quality = PromptImageQuality.Draft, Width = 768, Height = 512 });

        Assert.Equal(("http://localhost:7860/sdapi/v1/txt2img", HttpMethod.Post), (sent.Single().RequestUri!.ToString(), sent.Single().Method));
        JsonElement body = bodies.Single();
        Assert.Equal(("a red fox", "blurry", 6, 768, 512, 2, 5), (body.GetProperty("prompt").GetString(), body.GetProperty("negative_prompt").GetString(), body.GetProperty("steps").GetInt32(), body.GetProperty("width").GetInt32(), body.GetProperty("height").GetInt32(), body.GetProperty("batch_size").GetInt32(), body.GetProperty("seed").GetInt64()));
        Assert.Equal((3.5, true, false), (body.GetProperty("cfg_scale").GetDouble(), body.GetProperty("send_images").GetBoolean(), body.GetProperty("save_images").GetBoolean()));
        Assert.Equal([11L, 12L], images.Select(image => image.Seed));
        Assert.All(images, image => Assert.Equal(FakeImageGenerator.Png, image.Content.ToArray()));
    }

    [Theory]
    [InlineData((int)PromptImageQuality.Draft, 6)]
    [InlineData((int)PromptImageQuality.Standard, 20)]
    [InlineData((int)PromptImageQuality.High, 40)]
    public async Task Generate_TurnsTheQualityIntoTheStepsItWasGiven(int quality, int steps)
    {
        answer = _ => Json(Answer());

        await Generator().Generate(new PromptImageRequest { Prompt = "x", Quality = (PromptImageQuality)quality });

        Assert.Equal(steps, bodies.Single().GetProperty("steps").GetInt32());
    }

    [Fact]
    public async Task Generate_UsesTheSizeItWasGivenWhenNoneIsAskedForAndARandomSeedAndNoNegativePrompt()
    {
        answer = _ => Json(Answer(1, null));

        IReadOnlyList<PromptImageResult> images = await Generator(width: 512, height: 768).Generate(new PromptImageRequest { Prompt = "x" });

        JsonElement body = bodies.Single();
        Assert.Equal((512, 768, -1L), (body.GetProperty("width").GetInt32(), body.GetProperty("height").GetInt32(), body.GetProperty("seed").GetInt64()));
        Assert.False(body.TryGetProperty("negative_prompt", out _));
        Assert.Null(Assert.Single(images).Seed);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1, 64)]
    [InlineData(4096, 2048)]
    [InlineData(515, 512)]
    public async Task Generate_RoundsTheSizeToAMultipleOfEightWithinWhatTheServerTakes(int asked, int sent)
    {
        answer = _ => Json(Answer());

        await Generator().Generate(new PromptImageRequest { Prompt = "x", Width = asked, Height = asked });

        Assert.Equal((sent, sent), (bodies.Single().GetProperty("width").GetInt32(), bodies.Single().GetProperty("height").GetInt32()));
    }

    [Fact]
    public async Task Generate_LoadsTheCheckpointItWasGivenForTheImage()
    {
        answer = _ => Json(Answer());

        await Generator(PromptCapabilityModels.All("sdxl_turbo.safetensors")).Generate(new PromptImageRequest { Prompt = "x" });

        Assert.Equal(("sdxl_turbo.safetensors", false), (bodies.Single().GetProperty("override_settings").GetProperty("sd_model_checkpoint").GetString(), bodies.Single().GetProperty("override_settings_restore_afterwards").GetBoolean()));
    }

    [Fact]
    public async Task Generate_LimitsTheCountToFour()
    {
        answer = _ => Json(Answer());

        await Generator().Generate(new PromptImageRequest { Prompt = "x", Count = 50 });

        Assert.Equal(4, bodies.Single().GetProperty("batch_size").GetInt32());
    }

    [Theory]
    [InlineData("""{"error":"OutOfMemoryError","detail":"CUDA out of memory"}""", "OutOfMemoryError")]
    [InlineData("""{"detail":"Not Found"}""", "Not Found")]
    [InlineData("nothing useful", "500")]
    public async Task Generate_ReportsWhatTheServerSaysWentWrong(string body, string expected)
    {
        answer = _ => Json(body, HttpStatusCode.InternalServerError);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"images":[]}""")]
    [InlineData("""{"other":1}""")]
    public async Task Generate_ReportsAnAnswerWithoutAnImage(string body)
    {
        answer = _ => Json(body);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.Contains("without any image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_SaysToStartTheServerWithItsApiWhenItCannotBeReached()
    {
        answer = _ => throw new HttpRequestException("refused");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Generator().Generate(new PromptImageRequest { Prompt = "x" }));

        Assert.Contains("http://localhost:7860", exception.Message, StringComparison.Ordinal);
        Assert.Contains("API turned on", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PromptCapability.Fast, "fast.safetensors")]
    [InlineData(PromptCapability.Balanced, "balanced.safetensors")]
    [InlineData(PromptCapability.Strong, "strong.safetensors")]
    [InlineData(PromptCapability.Max, "max.safetensors")]
    public async Task Generate_LoadsTheCheckpointOfTheCapabilityTheRequestAsksFor(PromptCapability capability, string checkpoint)
    {
        answer = _ => Json($$"""{"images":["{{Convert.ToBase64String(FakeImageGenerator.Png)}}"]}""");
        PromptCapabilityModels models = new() { Fast = "fast.safetensors", Balanced = "balanced.safetensors", Strong = "strong.safetensors", Max = "max.safetensors" };

        await Generator(models).Generate(new PromptImageRequest { Prompt = "x", Capability = capability });

        Assert.Equal(checkpoint, bodies.Single().GetProperty("override_settings").GetProperty("sd_model_checkpoint").GetString());
    }

    [Fact]
    public async Task Generate_WithoutACapability_LoadsTheBalancedCheckpoint()
    {
        answer = _ => Json($$"""{"images":["{{Convert.ToBase64String(FakeImageGenerator.Png)}}"]}""");

        await Generator(new PromptCapabilityModels { Fast = "a", Balanced = "b", Strong = "c", Max = "d" }).Generate(new PromptImageRequest { Prompt = "x" });

        Assert.Equal("b", bodies.Single().GetProperty("override_settings").GetProperty("sd_model_checkpoint").GetString());
    }

    [Fact]
    public void Features_OfferTheCapabilityOnlyWhenTheCheckpointsDiffer()
    {
        Assert.False(Generator(PromptCapabilityModels.All("one")).Features.HasFlag(PromptImageFeatures.Capability));
        Assert.True(Generator(new PromptCapabilityModels { Fast = "a", Balanced = "b", Strong = "b", Max = "b" }).Features.HasFlag(PromptImageFeatures.Capability));
    }
}
