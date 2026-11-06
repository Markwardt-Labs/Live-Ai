namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptImageOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RefusesAnOpenAiKeyThatIsMissingOrBlank(string? key)
        => Assert.ThrowsAny<ArgumentException>(() => new OpenAiImageOptions { ApiKey = key }.Validate());

    [Fact]
    public void Validate_RefusesACapabilityThatIsNotDefined()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiImageOptions { ApiKey = "key", Capability = (PromptCapability)99 }.Validate());

    [Fact]
    public void Validate_AcceptsAnOpenAiKeyAndEveryCapability()
    {
        foreach (PromptCapability capability in Enum.GetValues<PromptCapability>())
        {
            new OpenAiImageOptions { ApiKey = "key", Capability = capability }.Validate();
        }
    }

    [Fact]
    public void Validate_RefusesSizesAndStepsBelowOneAndNegativeGuidance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StableDiffusionImageOptions { Width = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StableDiffusionImageOptions { Height = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StableDiffusionImageOptions { DraftSteps = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StableDiffusionImageOptions { StandardSteps = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StableDiffusionImageOptions { HighSteps = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new StableDiffusionImageOptions { Guidance = -0.5 }.Validate());
    }

    [Fact]
    public void Validate_AcceptsTheStableDiffusionDefaults()
        => new StableDiffusionImageOptions().Validate();

    [Fact]
    public void Validate_RefusesAKindOfOptionsTheLibraryDoesNotKnow()
        => Assert.Throws<ArgumentException>(() => new UnknownImageOptions().Validate());

    [Fact]
    public void CreateGenerator_ForOpenAi_MakesOneThatOffersTransparencyAndNeedsNoAdvice()
    {
        IPromptImageGenerator generator = new OpenAiImageOptions { ApiKey = "key" }.CreateGenerator();

        Assert.Equal((PromptImageFeatures.Transparency, null), (generator.Features, generator.PromptGuide));
    }

    [Fact]
    public void CreateGenerator_ForStableDiffusion_MakesOneWithANegativePromptASeedAndTheUsualAdviceOrTheGivenOne()
    {
        IPromptImageGenerator usual = new StableDiffusionImageOptions().CreateGenerator();
        IPromptImageGenerator plain = new StableDiffusionImageOptions { PromptGuide = "Write sentences." }.CreateGenerator();

        Assert.Equal(PromptImageFeatures.NegativePrompt | PromptImageFeatures.Seed, usual.Features);
        Assert.Contains("comma separated", usual.PromptGuide, StringComparison.Ordinal);
        Assert.Equal("Write sentences.", plain.PromptGuide);
    }

    [Fact]
    public void CreateGenerator_ForAKindOfOptionsTheLibraryDoesNotKnow_Fails()
        => Assert.Throws<ArgumentException>(() => new UnknownImageOptions().CreateGenerator());

    [Fact]
    public void Json_KeepsTheKindAndEveryValueTheApiKeyIncluded()
    {
        PromptImageOptions openAi = new OpenAiImageOptions { ApiKey = "secret", Capability = PromptCapability.Strong };
        PromptImageOptions stable = new StableDiffusionImageOptions { Server = new Uri("http://sd:7860/"), Model = "m", Width = 640, DraftSteps = 4, PromptGuide = "Be brief." };

        string openAiJson = JsonSerializer.Serialize(openAi);
        PromptImageOptions? openAiBack = JsonSerializer.Deserialize<PromptImageOptions>(openAiJson);
        PromptImageOptions? stableBack = JsonSerializer.Deserialize<PromptImageOptions>(JsonSerializer.Serialize(stable));

        Assert.Equal(openAi, openAiBack);
        Assert.Equal(stable, stableBack);
    }

    private sealed record UnknownImageOptions : PromptImageOptions;
}
