namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptProviderStaticsTests
{
    [Fact]
    public void Claude_IsOneSharedProviderOfClaudeCode()
    {
        Assert.Same(IPromptProvider.Claude, IPromptProvider.Claude);
        Assert.IsType<PromptProvider>(IPromptProvider.Claude);
    }

    [Fact]
    public void Codex_IsOneSharedProviderOfCodex()
    {
        Assert.Same(IPromptProvider.Codex, IPromptProvider.Codex);
        Assert.IsType<PromptProvider>(IPromptProvider.Codex);
    }

    [Fact]
    public void TheTwoStatics_AreIndependentProviders()
        => Assert.NotSame(IPromptProvider.Claude, IPromptProvider.Codex);
}
