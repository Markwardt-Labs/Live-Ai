namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptEngineTests
{
    [Fact]
    public void TheEnginesOfTheBuiltInTools_HaveTheDefaultSettings()
    {
        ClaudeEngine claude = new();
        CodexEngine codex = new();

        Assert.Equal(("Claude Code", "claude"), (claude.Identifier, claude.CreateTestStart(null).FileName));
        Assert.Equal(("Codex", "codex"), (codex.Identifier, codex.CreateTestStart(null).FileName));
    }

    [Fact]
    public void EnginesMadeWithOtherSettings_UseThem()
    {
        IPromptEngine claude = new ClaudeEngine("my-claude", "/claude");
        IPromptEngine codex = new CodexEngine("my-codex", "/codex");

        Assert.Equal(("my-claude", "/claude"), (claude.CreateTestStart(null).FileName, claude.CreateTestStart(null).Environment["CLAUDE_CONFIG_DIR"]));
        Assert.Equal(("my-codex", "/codex"), (codex.CreateTestStart(null).FileName, codex.CreateTestStart(null).Environment["CODEX_HOME"]));
    }

    [Fact]
    public void AnEngineThatImplementsOnlyTheRequiredMembers_RunsOneProcessPerPromptAndNeedsFilesAsFiles()
    {
        IPromptEngine engine = new ScriptEngine("tool");

        Assert.False(engine.CanPersist);
        Assert.False(engine.KeepsInstructions);
        Assert.False(engine.SchemaPerTurn);
        Assert.True(engine.NeedsFiles);
        Assert.False(engine.Accepts("image/png"));
        Assert.True(engine.HasSession("any"));
        Assert.Throws<NotSupportedException>(() => engine.CreatePersistentStart(new PromptEngineStart { Capability = PromptCapability.Fast, Reasoning = PromptReasoning.Low, WorkingFolder = "/", Instructions = string.Empty }));
        Assert.Throws<NotSupportedException>(() => engine.Attach(null!, new PromptEngineStart { Capability = PromptCapability.Fast, Reasoning = PromptReasoning.Low, WorkingFolder = "/", Instructions = string.Empty }));
    }

    [Fact]
    public void ThePromptOfAnEngineWithDefaults_CarriesTheNotesInFrontOfTheText()
    {
        IPromptEngine engine = new ScriptEngine("tool");
        PromptInput plain = new() { Text = "hello", Capability = PromptCapability.Balanced, Reasoning = PromptReasoning.Medium };
        PromptInput told = plain with { Notes = ["the build passes", "it is Tuesday"] };

        Assert.Equal("hello", engine.Plain(plain));
        Assert.Equal("Note from the host application: the build passes\n\nNote from the host application: it is Tuesday\n\nhello", engine.Plain(told));
        Assert.Equal(engine.Plain(told), engine.Frame(told));
    }

    [Fact]
    public async Task TheDefaultsOfAnEngine_HaveNoHistoryNothingToDeleteAndBranchOnlyAnEmptySession()
    {
        IPromptEngine engine = new ScriptEngine("tool");
        PromptEngineStart start = new() { Capability = PromptCapability.Fast, Reasoning = PromptReasoning.Low, WorkingFolder = "/", Instructions = string.Empty };

        Assert.Empty(await engine.ReadHistory("any").ToListAsync());
        await engine.DeleteSession("any");
        Assert.Equal(new PromptBranch(), await engine.Branch(start, null));
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.Branch(start with { AiSessionId = "abc" }, null));
        Assert.Equal("{\"a\":1}", engine.FormatSchema(JsonDocument.Parse("{\"a\":1}").RootElement));
    }

    [Fact]
    public void CreateTestStart_OfAnEngine_IsAFastLowSessionWithTheKey()
    {
        ScriptEngine script = new("tool");

        ((IPromptEngine)script).CreateTestStart("key");

        PromptEngineStart start = Assert.Single(script.Starts);
        Assert.Equal((PromptCapability.Fast, PromptReasoning.Low, "key"), (start.Capability, start.Reasoning, start.ApiKey));
        Assert.Equal(Path.GetTempPath(), start.WorkingFolder);
    }

    [Fact]
    public async Task GetWindows_OfAnEngineWithoutOne_IsNotSupported()
        => await Assert.ThrowsAsync<NotSupportedException>(() => ((IPromptEngine)new ScriptEngine("tool")).GetWindows());
}
