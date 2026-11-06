namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ApiKeyRedactionTests
{
    private readonly string key = "sk-very-secret-key";

    [Fact]
    public void TheSessionOptions_HideTheKeyWhenPrintedButKeepTheOtherMembers()
    {
        string claude = new ClaudeSessionOptions { ApiKey = key, Command = "my-claude", WorkspacePath = "/project" }.ToString();
        string codex = new CodexSessionOptions { ApiKey = key, ConfigFolder = "/codex" }.ToString();

        Assert.DoesNotContain(key, claude, StringComparison.Ordinal);
        Assert.DoesNotContain(key, codex, StringComparison.Ordinal);
        Assert.Contains("ApiKey = ***", claude, StringComparison.Ordinal);
        Assert.Contains("Command = my-claude", claude, StringComparison.Ordinal);
        Assert.Contains("WorkspacePath = /project", claude, StringComparison.Ordinal);
        Assert.Contains("ConfigFolder = /codex", codex, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionWithoutAKey_PrintsAnEmptyKey()
        => Assert.Contains("ApiKey = ,", new ClaudeSessionOptions().ToString(), StringComparison.Ordinal);

    [Fact]
    public void TheImageOptions_HideTheKeyWhenPrinted()
    {
        string text = new OpenAiImageOptions { ApiKey = key }.ToString();

        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.Contains("ApiKey = ***", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoredLinkAndTheInternalRecordsThatCarryTheKey_HideItToo()
    {
        PromptSessionLink link = new() { Id = Guid.NewGuid(), Provider = "Claude Code", WorkingFolder = "/w", Options = new ClaudeSessionOptions { ApiKey = key }, ImageGeneration = new OpenAiImageOptions { ApiKey = key } };

        Assert.DoesNotContain(key, link.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, new PromptProviderOptions { ApiKey = key }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, new PromptEngineStart { WorkingFolder = "/w", Instructions = "i", ApiKey = key }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, new OpenCodeModelProvider { Id = "p", BaseUrl = "http://x", Models = [], ApiKey = key }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Equality_StillComparesTheKey()
    {
        Assert.NotEqual(new ClaudeSessionOptions { ApiKey = "a" }, new ClaudeSessionOptions { ApiKey = "b" });
        Assert.Equal(new ClaudeSessionOptions { ApiKey = "a" }, new ClaudeSessionOptions { ApiKey = "a" });
    }
}
