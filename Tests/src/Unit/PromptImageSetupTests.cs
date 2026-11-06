namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptImageSetupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RefusesAnOpenAiKeyThatIsMissingOrBlank(string? key)
        => Assert.ThrowsAny<ArgumentException>(() => new OpenAiImageSetup { ApiKey = key! }.Validate());

    [Fact]
    public void OpenAiSetupStoredWithoutAKey_CannotBeRead()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PromptImageSetup>("""{"service":"openai"}"""));

    [Fact]
    public void Validate_AcceptsAnOpenAiKey()
        => new OpenAiImageSetup { ApiKey = "key" }.Validate();

    [Fact]
    public void Validate_RefusesAMissingCheckpointMapping()
        => Assert.Throws<ArgumentNullException>(() => new LocalImageSetup { Models = null! }.Validate());

    [Fact]
    public void Validate_RefusesACheckpointMappingWithABlankTitle()
        => Assert.Throws<ArgumentException>(() => new LocalImageSetup { Models = new PromptCapabilityModels { Fast = "a", Balanced = " ", Strong = "c", Max = "d" } }.Validate());

    [Fact]
    public void Validate_RefusesAStableDiffusionServerThatIsMissing()
        => Assert.Throws<ArgumentNullException>(() => new LocalImageSetup { Server = null!, Models = PromptCapabilityModels.All("a") }.Validate());

    [Fact]
    public void Validate_AcceptsAStableDiffusionServerWithACheckpoint()
        => new LocalImageSetup { Models = PromptCapabilityModels.All("a") }.Validate();

    [Fact]
    public void Validate_RefusesAKindOfSetupTheLibraryDoesNotKnow()
        => Assert.Throws<ArgumentException>(() => new UnknownImageSetup().Validate());

    [Fact]
    public void CreateGenerator_ForOpenAi_MakesOneThatOffersTransparencyAndACapabilityChoiceAndNeedsNoAdvice()
    {
        IPromptImageGenerator generator = new OpenAiImageSetup { ApiKey = "key" }.CreateGenerator();

        Assert.Equal((PromptImageFeatures.Transparency | PromptImageFeatures.Capability, null), (generator.Features, generator.PromptGuide));
    }

    [Fact]
    public void CreateGenerator_ForStableDiffusion_MakesOneWithANegativePromptASeedAndTheUsualAdvice()
    {
        IPromptImageGenerator generator = new LocalImageSetup { Models = PromptCapabilityModels.All("a") }.CreateGenerator();

        Assert.Equal(PromptImageFeatures.NegativePrompt | PromptImageFeatures.Seed, generator.Features);
        Assert.Contains("comma separated", generator.PromptGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateGenerator_ForAKindOfSetupTheLibraryDoesNotKnow_Fails()
        => Assert.Throws<ArgumentException>(() => new UnknownImageSetup().CreateGenerator());

    [Fact]
    public void Json_KeepsTheKindAndEveryValueTheApiKeyIncluded()
    {
        PromptImageSetup openAi = new OpenAiImageSetup { ApiKey = "secret" };
        PromptImageSetup stable = new LocalImageSetup { Server = new Uri("http://sd:7860/"), Models = new PromptCapabilityModels { Fast = "a", Balanced = "b", Strong = "c", Max = "d" } };

        string openAiJson = JsonSerializer.Serialize(openAi);
        PromptImageSetup? openAiBack = JsonSerializer.Deserialize<PromptImageSetup>(openAiJson);
        PromptImageSetup? stableBack = JsonSerializer.Deserialize<PromptImageSetup>(JsonSerializer.Serialize(stable));

        Assert.Equal(openAi, openAiBack);
        Assert.Equal(stable, stableBack);
    }

    [Fact]
    public void CreateGenerator_ForStableDiffusion_OffersACapabilityChoiceOnlyWithDifferentCheckpoints()
    {
        IPromptImageGenerator same = new LocalImageSetup { Models = PromptCapabilityModels.All("a") }.CreateGenerator();
        IPromptImageGenerator different = new LocalImageSetup { Models = new PromptCapabilityModels { Fast = "a", Balanced = "b", Strong = "c", Max = "d" } }.CreateGenerator();

        Assert.False(same.Features.HasFlag(PromptImageFeatures.Capability));
        Assert.True(different.Features.HasFlag(PromptImageFeatures.Capability));
    }

    private sealed record UnknownImageSetup : PromptImageSetup;
}
