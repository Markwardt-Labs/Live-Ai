namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptEngineTests
{
    [Fact]
    public async Task TheEnginesOfTheBuiltInTools_HaveTheDefaultSettings()
    {
        ClaudeEngine claude = new();
        CodexEngine codex = new("/nonexistent/codex");

        Assert.Equal(("Claude Code", "claude"), (claude.Identifier, (await claude.CreateTestStart(null)).FileName));
        Assert.Equal(("Codex", "/nonexistent/codex"), (codex.Identifier, (await codex.CreateTestStart(null)).FileName));
    }

    [Fact]
    public async Task EnginesMadeWithOtherSettings_UseThem()
    {
        IPromptEngine claude = new ClaudeEngine("my-claude", "/claude");
        IPromptEngine codex = new CodexEngine("my-codex", "/codex");
        ProcessStartInfo claudeStart = await claude.CreateTestStart(null);
        ProcessStartInfo codexStart = await codex.CreateTestStart(null);

        Assert.Equal(("my-claude", "/claude"), (claudeStart.FileName, claudeStart.Environment["CLAUDE_CONFIG_DIR"]));
        Assert.Equal(("my-codex", "/codex"), (codexStart.FileName, codexStart.Environment["CODEX_HOME"]));
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
        Assert.Throws<NotSupportedException>(() => engine.CreatePersistentStart(new PromptEngineStart { WorkingFolder = "/", Instructions = string.Empty }));
        Assert.Throws<NotSupportedException>(() => engine.Attach(null!, new PromptEngineStart { WorkingFolder = "/", Instructions = string.Empty }));
    }

    [Fact]
    public void ThePromptOfAnEngineWithDefaults_CarriesTheNotesInFrontOfTheText()
    {
        IPromptEngine engine = new ScriptEngine("tool");
        PromptInput plain = new() { Text = "hello", Model = "m" };
        PromptInput told = plain with { Notes = ["the build passes", "it is Tuesday"] };

        Assert.Equal("hello", engine.Plain(plain));
        Assert.Equal("Note from the host application: the build passes\n\nNote from the host application: it is Tuesday\n\nhello", engine.Plain(told));
        Assert.Equal(engine.Plain(told), engine.Frame(told));
    }

    [Fact]
    public async Task TheDefaultsOfAnEngine_HaveNoHistoryNothingToDeleteAndBranchOnlyAnEmptySession()
    {
        IPromptEngine engine = new ScriptEngine("tool");
        PromptEngineStart start = new() { WorkingFolder = "/", Instructions = string.Empty };

        Assert.Empty(await engine.ReadHistory("any").ToListAsync());
        await engine.DeleteSession("any");
        Assert.Equal(new PromptBranch(), await engine.Branch(start, null));
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.Branch(start with { AiSessionId = "abc" }, null));
        Assert.Equal("{\"a\":1}", engine.FormatSchema(JsonDocument.Parse("{\"a\":1}").RootElement));
    }

    [Fact]
    public async Task CreateTestStart_OfAnEngine_RunsItsFirstModelAtTheLowestSettingsWithTheKey()
    {
        ScriptEngine script = new("tool") { Models = [TestModels.Model, new PromptModel { Id = "other" }] };

        await ((IPromptEngine)script).CreateTestStart("key");

        PromptEngineStart start = Assert.Single(script.Starts);
        Assert.Equal(("test-model", "low", "false", "key"), (start.Model, start.Settings["effort"], start.Settings["fast"], start.ApiKey));
        Assert.Equal(Path.GetTempPath(), start.WorkingFolder);
    }

    [Fact]
    public async Task CreateTestStart_OfAnEngineWithoutModels_RunsNoModel()
    {
        ScriptEngine script = new("tool");

        await ((IPromptEngine)script).CreateTestStart(null);

        PromptEngineStart start = Assert.Single(script.Starts);
        Assert.Equal((null, 0), (start.Model, start.Settings.Count));
    }

    [Fact]
    public async Task GetModels_OfAnEngineWithoutOne_IsEmpty()
        => Assert.Empty(await ((IPromptEngine)new ScriptEngine("tool")).GetModels());

    [Fact]
    public async Task GetWindows_OfAnEngineWithoutOne_IsEmpty()
        => Assert.Empty(await ((IPromptEngine)new ScriptEngine("tool")).GetWindows());
}
