namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptCapabilityContextsTests
{
    [Theory]
    [InlineData(PromptCapability.Fast, 1)]
    [InlineData(PromptCapability.Balanced, 2)]
    [InlineData(PromptCapability.Strong, 3)]
    [InlineData(PromptCapability.Max, 4)]
    public void For_GivesTheSizeOfTheCapability(PromptCapability capability, int expected)
        => Assert.Equal(expected, new PromptCapabilityContexts { Fast = 1, Balanced = 2, Strong = 3, Max = 4 }.For(capability));

    [Fact]
    public void ACapabilityWithoutASize_GivesNone()
        => Assert.Equal((4096, null), (new PromptCapabilityContexts { Fast = 4096 }.For(PromptCapability.Fast), new PromptCapabilityContexts { Fast = 4096 }.For(PromptCapability.Max)));

    [Fact]
    public void All_GivesEveryCapabilityTheSameSize()
        => Assert.All(Enum.GetValues<PromptCapability>(), capability => Assert.Equal(8192, PromptCapabilityContexts.All(8192).For(capability)));
}
