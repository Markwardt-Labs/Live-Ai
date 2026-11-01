namespace Markwardt.LiveAi.Tests.Unit.Models;

public sealed class PromptModelsTests
{
    [Theory]
    [InlineData(PromptCapability.Fast, "fast")]
    [InlineData(PromptCapability.Balanced, "balanced")]
    [InlineData(PromptCapability.Max, "max")]
    public void For_FindsTheModelOfTheCapability(PromptCapability capability, string expected)
        => Assert.Equal(expected, new PromptModels { Default = "default", Fast = "fast", Balanced = "balanced", Max = "max" }.For(capability));

    [Fact]
    public void For_FallsBackToTheDefaultAndThenToNothing()
    {
        Assert.Equal("default", new PromptModels { Default = "default", Fast = "fast" }.For(PromptCapability.Max));
        Assert.Equal("fast", new PromptModels { Default = "default", Fast = "fast" }.For(PromptCapability.Fast));
        Assert.Null(new PromptModels { Fast = "fast" }.For(PromptCapability.Balanced));
        Assert.Null(new PromptModels().For(PromptCapability.Max));
    }

    [Fact]
    public void For_RefusesACapabilityThatIsNotDefined()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PromptModels().For((PromptCapability)99));

    [Fact]
    public void Only_UsesOneModelForEveryCapability()
    {
        PromptModels models = PromptModels.Only("opus");

        Assert.All(Enum.GetValues<PromptCapability>(), capability => Assert.Equal("opus", models.For(capability)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Only_RefusesAnEmptyName(string name)
        => Assert.Throws<ArgumentException>(() => PromptModels.Only(name));

    [Fact]
    public void ByCapability_AssignsEachCapabilityAndLeavesTheOthersToTheTool()
    {
        PromptModels models = PromptModels.ByCapability([(PromptCapability.Fast, "a"), (PromptCapability.Max, "c")]);

        Assert.Equal(("a", null, "c"), (models.For(PromptCapability.Fast), models.For(PromptCapability.Balanced), models.For(PromptCapability.Max)));
        Assert.Null(models.Default);
    }

    [Fact]
    public void ByCapability_RefusesACapabilityGivenTwice()
        => Assert.Throws<ArgumentException>(() => PromptModels.ByCapability([(PromptCapability.Fast, "a"), (PromptCapability.Fast, "b")]));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ByCapability_RefusesAnEmptyName(string name)
        => Assert.Throws<ArgumentException>(() => PromptModels.ByCapability([(PromptCapability.Fast, name)]));
}
