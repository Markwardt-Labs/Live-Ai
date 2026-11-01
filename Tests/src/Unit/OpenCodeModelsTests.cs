namespace Markwardt.LiveAi.Tests.Unit;

public sealed class OpenCodeModelsTests
{
    [Fact]
    public void ByCapability_MapsEachCapabilityToItsModel()
    {
        OpenCodeModels models = OpenCodeModels.ByCapability([(PromptCapability.Fast, "small:1b"), (PromptCapability.Balanced, "mid:8b"), (PromptCapability.Max, "big:26b")]);

        Assert.Equal(("ollama/small:1b", "ollama/mid:8b", "ollama/big:26b"), (models.Fast, models.Balanced, models.Max));
    }

    [Fact]
    public void ByCapability_AddsEachLocalModelOnceAndLeavesQualifiedNamesToOpenCode()
    {
        OpenCodeModels models = OpenCodeModels.ByCapability([(PromptCapability.Fast, "small:1b"), (PromptCapability.Balanced, "small:1b"), (PromptCapability.Max, "anthropic/claude-sonnet")]);

        OpenCodeModelProvider provider = Assert.Single(models.Providers);
        Assert.Equal(("ollama", "http://localhost:11434/v1"), (provider.Id, provider.BaseUrl));
        Assert.Equal(["small:1b"], provider.Models.Select(model => model.Id));
        Assert.Equal(("ollama/small:1b", "ollama/small:1b", "anthropic/claude-sonnet"), (models.Fast, models.Balanced, models.Max));
    }

    [Fact]
    public void ByCapability_WithOnlyQualifiedNamesAddsNoProvider()
        => Assert.Empty(OpenCodeModels.ByCapability([(PromptCapability.Max, "openai/gpt")]).Providers);

    [Fact]
    public void ByCapability_WithNothingLeavesEverythingToOpenCode()
    {
        OpenCodeModels models = OpenCodeModels.ByCapability([]);

        Assert.Equal((null, null, null, null), (models.Fast, models.Balanced, models.Max, models.Default));
        Assert.Empty(models.Providers);
    }

    [Fact]
    public void ByCapability_AcceptsAnyEnumerable()
        => Assert.Equal("ollama/m", OpenCodeModels.ByCapability(new List<(PromptCapability, string)> { (PromptCapability.Balanced, "m") }.Where(_ => true)).Balanced);

    [Fact]
    public void ByCapability_RefusesACapabilityGivenTwice()
        => Assert.Throws<ArgumentException>(() => OpenCodeModels.ByCapability([(PromptCapability.Fast, "a"), (PromptCapability.Fast, "b")]));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ByCapability_RefusesAnEmptyName(string name)
        => Assert.Throws<ArgumentException>(() => OpenCodeModels.ByCapability([(PromptCapability.Fast, name)]));

    [Theory]
    [InlineData(PromptCapability.Fast, "p/fast")]
    [InlineData(PromptCapability.Balanced, "p/base")]
    [InlineData(PromptCapability.Max, "p/max")]
    public void For_FindsTheModelOfTheCapability(PromptCapability capability, string expected)
        => Assert.Equal(expected, new OpenCodeModels { Default = "p/base", Fast = "p/fast", Max = "p/max" }.For(capability));

    [Fact]
    public void For_FallsBackToTheDefaultAndThenToNothing()
    {
        Assert.Equal("p/base", new OpenCodeModels { Default = "p/base" }.For(PromptCapability.Max));
        Assert.Null(new OpenCodeModels().For(PromptCapability.Fast));
        Assert.Equal("p/fast", new OpenCodeModels { Fast = "p/fast" }.For(PromptCapability.Fast));
        Assert.Null(new OpenCodeModels { Fast = "p/fast" }.For(PromptCapability.Balanced));
    }

    [Fact]
    public void Configured_UsesOneModelOfOpenCodesOwnConfigurationForEverythingAndAddsNoProvider()
    {
        OpenCodeModels models = OpenCodeModels.Configured("anthropic/claude-sonnet");

        Assert.Equal("anthropic/claude-sonnet", models.For(PromptCapability.Max));
        Assert.Empty(models.Providers);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Configured_RefusesAnEmptyName(string name)
        => Assert.Throws<ArgumentException>(() => OpenCodeModels.Configured(name));

    [Fact]
    public void Ollama_UsesOneLocalModelForEverythingWithTheAddressAndTheContextSize()
    {
        OpenCodeModels models = OpenCodeModels.Ollama("gemma4:26b", "http://box:11434/v1", 8192);

        Assert.Equal("ollama/gemma4:26b", models.For(PromptCapability.Fast));
        OpenCodeModelProvider provider = Assert.Single(models.Providers);
        Assert.Equal(("ollama", "http://box:11434/v1", "gemma4:26b", 8192), (provider.Id, provider.BaseUrl, provider.Models[0].Id, provider.Models[0].ContextLimit));
    }

    [Fact]
    public void TheRecordIsAllTheFactoriesDoAndAdvancedModelsAreWrittenWithItsProperties()
    {
        OpenCodeModels models = new()
        {
            Default = "lmstudio/qwen3-coder",
            Fast = "ollama/gemma4:e2b",
            Providers =
            [
                new OpenCodeModelProvider { Id = "lmstudio", BaseUrl = "http://localhost:1234/v1", Models = [new OpenCodeModel { Id = "qwen3-coder", ContextLimit = 32768 }] },
                OpenCodeModelProvider.Ollama("gemma4:e2b")
            ]
        };

        Assert.Equal(["lmstudio", "ollama"], models.Providers.Select(provider => provider.Id));
        Assert.Equal("ollama/gemma4:e2b", models.For(PromptCapability.Fast));
        Assert.Equal("lmstudio/qwen3-coder", models.For(PromptCapability.Max));
    }
}
