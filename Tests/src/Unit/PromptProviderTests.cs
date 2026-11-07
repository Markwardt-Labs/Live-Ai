namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptProviderTests
{
    [Fact]
    public void EachEngineSaysWhichProviderItIs()
        => Assert.Equal([PromptProvider.Claude, PromptProvider.Codex, PromptProvider.Local], new IPromptEngine[] { new ClaudeEngine(), new CodexEngine(), new OpenCodeEngine() }.Select(engine => engine.Provider));

    [Fact]
    public async Task TheFakePrompter_MakesSessionsThatReportTheProviderOfTheirSetupAndKeepItInForks()
    {
        FakePrompter prompter = new();

        FakePromptSession claude = (FakePromptSession)await prompter.Create(new ClaudeSetup());
        FakePromptSession codex = (FakePromptSession)await prompter.Create(new CodexSetup());
        FakePromptSession local = (FakePromptSession)await prompter.Create(new LocalSetup());
        IPromptSession fork = await local.Fork();

        Assert.Equal([PromptProvider.Claude, PromptProvider.Codex, PromptProvider.Local, PromptProvider.Local], new[] { claude.Provider, codex.Provider, local.Provider, fork.Provider });
        Assert.Equal(PromptProvider.Claude, new FakePromptSession().Provider);
    }
}
