namespace Markwardt.LiveAi.Tests.Unit;

public sealed class OpenCodeEngineTests
{
    private readonly OpenCodeEngine engine = new("opencode", []);

    private IPromptEngine Generic => engine;

    [Fact]
    public async Task TheEngineIsDrivenThroughItsProtocolServerAndKeepsItsProcessBetweenPrompts()
    {
        Assert.Equal("OpenCode", engine.Identifier);
        Assert.True(engine.CanPersist);
        Assert.False(engine.NeedsFiles);
        Assert.False(engine.KeepsInstructions);
        Assert.True(engine.SchemaPerTurn);
        Assert.True(Generic.Accepts("image/png"));
        Assert.False(Generic.Accepts("application/pdf"));
        Assert.Throws<NotSupportedException>(() => engine.CreateStart(new PromptEngineStart { WorkingFolder = "/", Instructions = string.Empty }));
        Assert.Empty(await Generic.GetWindows());
    }

    [Theory]
    [InlineData("liveai", "liveai")]
    [InlineData("my.server", "my_server")]
    [InlineData("a b-c", "a_b-c")]
    public void AServerNameBecomesThePrefixOfItsToolsTheWayOpenCodeNormalizesIt(string name, string expected)
        => Assert.Equal(expected, engine.ToolPrefix(name));

    [Fact]
    public void ALineOfTheTestRunBecomesTheReplyTheUsageAndTheEnd()
    {
        Assert.Empty(engine.Parse("""{"type":"step_start","part":{"type":"step-start"}}"""));
        Assert.Equal("1", Assert.Single(engine.Parse("""{"type":"text","part":{"type":"text","text":"1"}}""")).Text);
        Assert.Empty(engine.Parse("""{"type":"step_finish","part":{"reason":"tool-calls"}}"""));

        IReadOnlyList<PromptEngineEvent> finished = engine.Parse("""{"type":"step_finish","part":{"reason":"stop","tokens":{"input":3,"output":1,"cache":{"read":2,"write":0}}}}""");

        Assert.Equal(PromptEngineEventKind.Finished, finished[1].Kind);
        Assert.Equal(new PromptUsage { InputTokens = 3, CachedInputTokens = 2, OutputTokens = 1 }, Assert.IsType<PromptUsageEvent>(finished[0].Event).Usage);
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"name":"APIError","data":{"message":"model unreachable"}}}""", "APIError")]
    [InlineData("""{"type":"error","error":{"message":"bad key"}}""", "bad key")]
    [InlineData("""{"type":"error","error":"plain failure"}""", "plain failure")]
    [InlineData("""{"type":"error"}""", "OpenCode failed.")]
    public void AnErrorLineBecomesAFailureWithItsMessage(string line, string expected)
    {
        PromptEngineEvent failure = Assert.Single(engine.Parse(line));

        Assert.Equal((PromptEngineEventKind.Failed, expected), (failure.Kind, failure.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"no":"type"}""")]
    [InlineData("""{"type":"unknown"}""")]
    public void ALineThatIsNotAnEventIsIgnored(string line)
        => Assert.Empty(engine.Parse(line));

    private OpenCodeEngine WithProvider(params OpenCodeModel[] models)
        => new("/nonexistent/opencode", [], [new OpenCodeModelProvider { Id = "x", BaseUrl = "http://localhost/v1", Models = models }]);

    [Fact]
    public async Task TheStartOfTheTestRunsTheFirstModelWithoutAServerOfItsOwn()
    {
        ProcessStartInfo start = await WithProvider(new OpenCodeModel { Id = "small" }, new OpenCodeModel { Id = "big" }).CreateTestStart("key");

        Assert.Equal(["run", "--standalone", "--format", "json"], start.ArgumentList);
        JsonElement config = JsonDocument.Parse(start.Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement;
        Assert.Equal("x/small", config.GetProperty("model").GetString());
        Assert.False(config.TryGetProperty("mcp", out _));
        Assert.False(config.TryGetProperty("permissions", out _));
    }

    [Fact]
    public async Task TheApiKeyOfTheSessionIsGivenToAProviderThatHasNone()
    {
        OpenCodeEngine keyed = WithProvider(new OpenCodeModel { Id = "m" });

        JsonElement config = JsonDocument.Parse((await keyed.CreateTestStart("secret")).Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement;

        Assert.Equal("secret", config.GetProperty("provider").GetProperty("x").GetProperty("options").GetProperty("apiKey").GetString());
        Assert.Equal("none", JsonDocument.Parse((await keyed.CreateTestStart(null)).Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement.GetProperty("provider").GetProperty("x").GetProperty("options").GetProperty("apiKey").GetString());
    }

    [Fact]
    public async Task ALimitThatIsGivenAloneIsCompletedBecauseOpenCodeDropsAModelWithAnIncompleteOne()
    {
        OpenCodeEngine limited = WithProvider(new OpenCodeModel { Id = "small", ContextLimit = 4000 }, new OpenCodeModel { Id = "capped", OutputLimit = 100 }, new OpenCodeModel { Id = "plain" });

        JsonElement models = JsonDocument.Parse((await limited.CreateTestStart(null)).Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement.GetProperty("provider").GetProperty("x").GetProperty("models");

        Assert.Equal((4000, 2000), (models.GetProperty("small").GetProperty("limit").GetProperty("context").GetInt32(), models.GetProperty("small").GetProperty("limit").GetProperty("output").GetInt32()));
        Assert.Equal((32768, 100), (models.GetProperty("capped").GetProperty("limit").GetProperty("context").GetInt32(), models.GetProperty("capped").GetProperty("limit").GetProperty("output").GetInt32()));
        Assert.False(models.GetProperty("plain").TryGetProperty("limit", out _));
    }

    [Fact]
    public void TheModelOfALocalServerGetsItsProviderAndTheContextSizeOfItsSetting()
    {
        OpenCodeEngine local = new("opencode", ["http://localhost:11434/v1", "http://localhost:1234/v1"]);
        PromptEngineStart given = new() { Model = "ollama/gemma4:26b", Settings = new Dictionary<string, string> { ["context"] = "8192", ["variant"] = "high" }, WorkingFolder = "/", Instructions = string.Empty };

        JsonElement config = JsonDocument.Parse(local.CreatePersistentStart(given).Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement;

        Assert.Equal("ollama/gemma4:26b", config.GetProperty("model").GetString());
        JsonElement provider = config.GetProperty("provider").GetProperty("ollama");
        Assert.Equal("http://localhost:11434/v1", provider.GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Equal(8192, provider.GetProperty("models").GetProperty("gemma4:26b").GetProperty("limit").GetProperty("context").GetInt32());
        Assert.Single(config.GetProperty("provider").EnumerateObject());
    }

    [Fact]
    public void TheModelOfAnotherLocalServerIsFoundByItsProviderIdentifier()
    {
        OpenCodeEngine local = new("opencode", ["http://localhost:11434/v1", "http://127.0.0.1:1234/v1"]);
        PromptEngineStart given = new() { Model = "local-1234/qwen3-coder", WorkingFolder = "/", Instructions = string.Empty };

        JsonElement config = JsonDocument.Parse(local.CreatePersistentStart(given).Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement;

        Assert.Equal("http://127.0.0.1:1234/v1", config.GetProperty("provider").GetProperty("local-1234").GetProperty("options").GetProperty("baseURL").GetString());
        Assert.False(config.GetProperty("provider").GetProperty("local-1234").GetProperty("models").GetProperty("qwen3-coder").TryGetProperty("limit", out _));
    }

    [Fact]
    public void AModelOfOpenCodesOwnConfigurationAddsNoProvider()
    {
        OpenCodeEngine local = new("opencode", ["http://localhost:11434/v1"]);
        PromptEngineStart given = new() { Model = "anthropic/claude-sonnet", WorkingFolder = "/", Instructions = string.Empty };

        JsonElement config = JsonDocument.Parse(local.CreatePersistentStart(given).Environment["OPENCODE_CONFIG_CONTENT"]!).RootElement;

        Assert.Equal("anthropic/claude-sonnet", config.GetProperty("model").GetString());
        Assert.False(config.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task TheModelsOfTheProvidersItWasGivenAreListedWithoutAnySettings()
    {
        PromptModel model = Assert.Single(await WithProvider(new OpenCodeModel { Id = "small", Name = "Small", ContextLimit = 4000 }).GetModels());

        Assert.Equal(("x/small", "Small", 4000), (model.Id, model.Name, model.ContextLimit));
        Assert.Empty(model.Settings!);
    }

    [Fact]
    public void AnExportedMessageBecomesTheEntriesOfTheHistory()
    {
        JsonElement user = JsonDocument.Parse("""{"type":"user","text":"hello","time":{"created":5000}}""").RootElement;
        JsonElement idle = JsonDocument.Parse("""{"type":"idle","time":{"created":6000}}""").RootElement;
        JsonElement empty = JsonDocument.Parse("""{"type":"assistant","content":[{"type":"text","text":""},{"type":"unknown"}]}""").RootElement;

        PromptHistoryEntry entry = Assert.Single(engine.Entries(user));

        Assert.Equal(new PromptInputEvent { Text = "hello" }, entry.Event);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(5000), entry.Time);
        Assert.Empty(engine.Entries(idle));
        Assert.Empty(engine.Entries(empty));
    }

    private IReadOnlyList<JsonElement> Messages()
        => [.. JsonDocument.Parse("""
            [{"id":"u1","type":"user","time":{"created":1000}},{"id":"a1","type":"assistant","time":{"created":2000}},{"id":"i1","type":"idle","time":{"created":2500}},
             {"id":"u2","type":"user","time":{"created":3000}},{"id":"a2","type":"assistant","time":{"created":4000}}]
            """).RootElement.EnumerateArray().Select(message => message.Clone())];

    [Theory]
    [InlineData(1000, "u2")]
    [InlineData(2000, "u2")]
    [InlineData(2600, "u2")]
    [InlineData(3000, null)]
    [InlineData(4000, null)]
    public void TheBranchAtAPointStopsBeforeTheNextInputAndTakesAllWhenThereIsNone(long position, string? expected)
        => Assert.Equal(expected, engine.Boundary(Messages(), new PromptHistoryPoint(position)));

    [Fact]
    public void ThePointBeforeEveryMessageIsNotInTheConversation()
        => Assert.Throws<PromptSessionException>(() => engine.Boundary(Messages(), new PromptHistoryPoint(500)));

    [Fact]
    public void ATokenCountThatIsMissingIsZero()
    {
        Assert.Equal(new PromptUsage(), engine.Spent(JsonDocument.Parse("{}").RootElement));
        Assert.Equal(0, engine.Count(JsonDocument.Parse("""{"n":"x"}""").RootElement, "n"));
    }
}
