namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptImageOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RefusesAnOpenAiKeyThatIsMissingOrBlank(string? key)
        => Assert.ThrowsAny<ArgumentException>(() => new OpenAiImageOptions { ApiKey = key! }.Validate());

    [Fact]
    public void OpenAiOptionsStoredWithoutAKey_CannotBeRead()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PromptImageOptions>("""{"service":"openai"}"""));

    [Fact]
    public void Validate_AcceptsAnOpenAiKey()
        => new OpenAiImageOptions { ApiKey = "key" }.Validate();

    [Fact]
    public void Validate_RefusesAMissingCheckpointMapping()
        => Assert.Throws<ArgumentNullException>(() => new StableDiffusionImageOptions { Models = null! }.Validate());

    [Fact]
    public void Validate_RefusesACheckpointMappingWithABlankTitle()
        => Assert.Throws<ArgumentException>(() => new StableDiffusionImageOptions { Models = new PromptCapabilityModels { Fast = "a", Balanced = " ", Strong = "c", Max = "d" } }.Validate());

    [Fact]
    public void Validate_RefusesAStableDiffusionServerThatIsMissing()
        => Assert.Throws<ArgumentNullException>(() => new StableDiffusionImageOptions { Server = null!, Models = PromptCapabilityModels.All("a") }.Validate());

    [Fact]
    public void Validate_AcceptsAStableDiffusionServerWithACheckpoint()
        => new StableDiffusionImageOptions { Models = PromptCapabilityModels.All("a") }.Validate();

    [Fact]
    public void Validate_RefusesAKindOfOptionsTheLibraryDoesNotKnow()
        => Assert.Throws<ArgumentException>(() => new UnknownImageOptions().Validate());

    [Fact]
    public void CreateGenerator_ForOpenAi_MakesOneThatOffersTransparencyAndACapabilityChoiceAndNeedsNoAdvice()
    {
        IPromptImageGenerator generator = new OpenAiImageOptions { ApiKey = "key" }.CreateGenerator();

        Assert.Equal((PromptImageFeatures.Transparency | PromptImageFeatures.Capability, null), (generator.Features, generator.PromptGuide));
    }

    [Fact]
    public void CreateGenerator_ForStableDiffusion_MakesOneWithANegativePromptASeedAndTheUsualAdvice()
    {
        IPromptImageGenerator generator = new StableDiffusionImageOptions { Models = PromptCapabilityModels.All("a") }.CreateGenerator();

        Assert.Equal(PromptImageFeatures.NegativePrompt | PromptImageFeatures.Seed, generator.Features);
        Assert.Contains("comma separated", generator.PromptGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateGenerator_ForAKindOfOptionsTheLibraryDoesNotKnow_Fails()
        => Assert.Throws<ArgumentException>(() => new UnknownImageOptions().CreateGenerator());

    [Fact]
    public void Json_KeepsTheKindAndEveryValueTheApiKeyIncluded()
    {
        PromptImageOptions openAi = new OpenAiImageOptions { ApiKey = "secret" };
        PromptImageOptions stable = new StableDiffusionImageOptions { Server = new Uri("http://sd:7860/"), Models = new PromptCapabilityModels { Fast = "a", Balanced = "b", Strong = "c", Max = "d" } };

        string openAiJson = JsonSerializer.Serialize(openAi);
        PromptImageOptions? openAiBack = JsonSerializer.Deserialize<PromptImageOptions>(openAiJson);
        PromptImageOptions? stableBack = JsonSerializer.Deserialize<PromptImageOptions>(JsonSerializer.Serialize(stable));

        Assert.Equal(openAi, openAiBack);
        Assert.Equal(stable, stableBack);
    }

    [Fact]
    public void CreateGenerator_ForStableDiffusion_OffersACapabilityChoiceOnlyWithDifferentCheckpoints()
    {
        IPromptImageGenerator same = new StableDiffusionImageOptions { Models = PromptCapabilityModels.All("a") }.CreateGenerator();
        IPromptImageGenerator different = new StableDiffusionImageOptions { Models = new PromptCapabilityModels { Fast = "a", Balanced = "b", Strong = "c", Max = "d" } }.CreateGenerator();

        Assert.False(same.Features.HasFlag(PromptImageFeatures.Capability));
        Assert.True(different.Features.HasFlag(PromptImageFeatures.Capability));
    }

    private sealed record UnknownImageOptions : PromptImageOptions;
}
