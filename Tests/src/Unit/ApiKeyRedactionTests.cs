namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ApiKeyRedactionTests
{
    private readonly string key = "sk-very-secret-key";

    [Fact]
    public void TheSetup_HideTheKeyWhenPrintedButKeepTheOtherMembers()
    {
        string claude = new ClaudeSetup { ApiKey = key, WorkspacePath = "/project" }.ToString();
        string codex = new CodexSetup { ApiKey = key }.ToString();

        Assert.DoesNotContain(key, claude, StringComparison.Ordinal);
        Assert.DoesNotContain(key, codex, StringComparison.Ordinal);
        Assert.Contains("ApiKey = ***", claude, StringComparison.Ordinal);
        Assert.Contains("WorkspacePath = /project", claude, StringComparison.Ordinal);
        Assert.Contains("ApiKey = ***", codex, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnvironment_HidesItsKeysWhenPrintedButKeepsTheOtherMembers()
    {
        string text = new PromptEnvironment { ClaudeApiKey = key, CodexApiKey = key, ClaudeCommand = "my-claude", ImageGeneration = new OpenAiImageSetup { ApiKey = key } }.ToString();

        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.Contains("ClaudeApiKey = ***", text, StringComparison.Ordinal);
        Assert.Contains("CodexApiKey = ***", text, StringComparison.Ordinal);
        Assert.Contains("ClaudeCommand = my-claude", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionWithoutAKey_PrintsAnEmptyKey()
        => Assert.EndsWith("ApiKey =  }", new ClaudeSetup().ToString(), StringComparison.Ordinal);

    [Fact]
    public void TheImageSetups_HideTheKeyWhenPrinted()
    {
        string text = new OpenAiImageSetup { ApiKey = key }.ToString();

        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.Contains("ApiKey = ***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoredLinkAndTheInternalRecordsThatCarryTheKey_HideItToo()
    {
        PromptSessionLink link = new() { LastLoaded = TestModels.Loaded, Id = Guid.NewGuid(), Provider = PromptProvider.Claude, WorkingFolder = "/w", Setup = new ClaudeSetup { ApiKey = key } };

        Assert.DoesNotContain(key, link.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, new PromptEnvironment { ImageGeneration = new OpenAiImageSetup { ApiKey = key } }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, new PromptToolServiceOptions { ApiKey = key }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, new PromptEngineStart { WorkingFolder = "/w", Instructions = "i", ApiKey = key }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, new OpenCodeModelProvider { Id = "p", BaseUrl = "http://x", Models = [], ApiKey = key }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Equality_StillComparesTheKey()
    {
        Assert.NotEqual(new ClaudeSetup { ApiKey = "a" }, new ClaudeSetup { ApiKey = "b" });
        Assert.Equal(new ClaudeSetup { ApiKey = "a" }, new ClaudeSetup { ApiKey = "a" });
    }
}
