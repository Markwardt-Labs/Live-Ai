namespace Markwardt.LiveAi.Tests.Unit;

public sealed class CodexEngineTests
{
    private readonly CodexEngine engine = new("codex");

    private PromptSessionRecord Record(Action<PromptSessionRecordBuilder>? change = null)
    {
        PromptSessionRecordBuilder builder = new();
        change?.Invoke(builder);
        return builder.Build(engine);
    }

    [Fact]
    public void CreatePersistentStart_DefaultsToTheAppServerWithAReadOnlySandboxAndNoShell()
    {
        ProcessStartInfo start = engine.CreatePersistentStart(Record().ToStart(null, null, null));

        Assert.Equal("codex", start.FileName);
        Assert.Equal("/work", start.WorkingDirectory);
        Assert.Equal(["app-server", "--disable", "shell_tool", "--disable", "multi_agent", "-c", "sandbox_mode=\"read-only\"", "-c", "approval_policy=\"never\""], start.ArgumentList);
    }

    [Fact]
    public void CreatePersistentStart_PassesTheApiKeyInTheEnvironment()
    {
        ProcessStartInfo start = engine.CreatePersistentStart(Record().ToStart("key", null, null));

        Assert.Equal("key", start.Environment["CODEX_API_KEY"]);
        Assert.DoesNotContain("key", start.ArgumentList);
    }

    [Fact]
    public void CreatePersistentStart_PassesAChosenConfigFolderInTheEnvironment()
        => Assert.Equal("/state", new CodexEngine("codex", "/state").CreatePersistentStart(Record().ToStart(null, null, null)).Environment["CODEX_HOME"]);

    [Fact]
    public void CreatePersistentStart_WithTools_ConfiguresTheServerAndPassesTheSecretInTheEnvironment()
    {
        ProcessStartInfo start = engine.CreatePersistentStart(Record().ToStart(null, null, Tools()));

        Assert.Contains("mcp_servers.liveai.url=\"http://127.0.0.1:5555/mcp\"", start.ArgumentList);
        Assert.Contains("mcp_servers.liveai.bearer_token_env_var=\"LIVEAI_TOOLS_TOKEN\"", start.ArgumentList);
        Assert.Equal("secret-token", start.Environment["LIVEAI_TOOLS_TOKEN"]);
        Assert.DoesNotContain(start.ArgumentList, argument => argument.Contains("secret-token"));
    }

    [Fact]
    public void CreatePersistentStart_WithExternalServers_ConfiguresEachOfThemAndApprovesTheirTools()
    {
        PromptSessionRecord record = Record(builder => builder.McpServers = new Dictionary<string, PromptMcpServer>
        {
            ["files"] = PromptMcpServer.Stdio("npx", "-y", "se\"rver") with { EnvironmentVariables = new Dictionary<string, string> { ["KEY"] = "va\\lue" } },
            ["web"] = PromptMcpServer.Http("https://example.com/mcp", "WEB_TOKEN")
        });

        IList<string> arguments = engine.CreatePersistentStart(record.ToStart(null, null, null)).ArgumentList;

        Assert.Contains("mcp_servers.files.command=\"npx\"", arguments);
        Assert.Contains("mcp_servers.files.args=[\"-y\", \"se\\\"rver\"]", arguments);
        Assert.Contains("mcp_servers.files.env={\"KEY\" = \"va\\\\lue\"}", arguments);
        Assert.Contains("mcp_servers.files.default_tools_approval_mode=\"approve\"", arguments);
        Assert.Contains("mcp_servers.web.url=\"https://example.com/mcp\"", arguments);
        Assert.Contains("mcp_servers.web.bearer_token_env_var=\"WEB_TOKEN\"", arguments);
        Assert.Contains("mcp_servers.web.default_tools_approval_mode=\"approve\"", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("mcp_servers.liveai"));
    }

    [Fact]
    public void CreatePersistentStart_WithAServerWithoutArgumentsOrEnvironment_SendsEmptyArguments()
    {
        IList<string> arguments = engine.CreatePersistentStart(Record(builder => builder.McpServers = new Dictionary<string, PromptMcpServer> { ["solo"] = PromptMcpServer.Stdio("tool") }).ToStart(null, null, null)).ArgumentList;

        Assert.Contains("mcp_servers.solo.args=[]", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("mcp_servers.solo.env"));
    }

    [Fact]
    public void CreatePersistentStart_HasNoSubAgents()
        => Assert.Equal(["--disable", "multi_agent"], engine.CreatePersistentStart(Record().ToStart(null, null, null)).ArgumentList.Skip(engine.CreatePersistentStart(Record().ToStart(null, null, null)).ArgumentList.ToList().IndexOf("multi_agent") - 1).Take(2));

    [Fact]
    public void CreatePersistentStart_WithoutTools_ConfiguresNoServer()
        => Assert.DoesNotContain(engine.CreatePersistentStart(Record().ToStart(null, null, null)).ArgumentList, argument => argument.StartsWith("mcp_servers"));

    [Fact]
    public void CreatePersistentStart_HasNoShellAndTheSandboxRefusesNativeWrites()
    {
        ProcessStartInfo start = engine.CreatePersistentStart(new PromptSessionRecordBuilder().Build(engine).ToStart(null, null, null));

        Assert.Equal(["--disable", "shell_tool"], start.ArgumentList.Skip(start.ArgumentList.IndexOf("--disable")).Take(2));
        Assert.Contains("sandbox_mode=\"read-only\"", start.ArgumentList);
        Assert.Contains("approval_policy=\"never\"", start.ArgumentList);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", start.ArgumentList);
    }

    [Fact]
    public void CreatePersistentStart_WithTools_ApprovesThemWithoutAskingBecauseTheLibraryAlreadyDecidedWhichAreOffered()
    {
        Assert.Contains("mcp_servers.liveai.default_tools_approval_mode=\"approve\"", engine.CreatePersistentStart(Record().ToStart(null, null, Tools())).ArgumentList);
        Assert.Contains("mcp_servers.liveai.tool_timeout_sec=3700", engine.CreatePersistentStart(Record().ToStart(null, null, Tools())).ArgumentList);
        Assert.DoesNotContain(engine.CreatePersistentStart(Record().ToStart(null, null, null)).ArgumentList, argument => argument.Contains("approval_mode"));
    }

    [Fact]
    public void FormatSchema_MakesEveryObjectStrictAtEveryLevel()
    {
        JsonElement schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "tags": { "type": "array", "items": { "type": "object", "properties": { "label": { "type": "string" }, "weight": { "type": "integer" } }, "required": ["label"] } },
                "extra": { "type": ["object", "null"], "properties": { "x": { "type": "integer" } } },
                "either": { "anyOf": [ { "type": "object", "properties": { "y": { "type": "string" } } }, { "type": "null" } ] }
              },
              "required": ["name"],
              "$defs": { "Shared": { "type": "object", "properties": { "z": { "type": "string" } } } }
            }
            """).RootElement;

        JsonElement formatted = JsonDocument.Parse(engine.FormatSchema(schema)).RootElement;

        Assert.False(formatted.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["name", "tags", "extra", "either"], formatted.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        JsonElement properties = formatted.GetProperty("properties");
        JsonElement tag = properties.GetProperty("tags").GetProperty("items");
        Assert.False(tag.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["label", "weight"], tag.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.False(properties.GetProperty("extra").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["x"], properties.GetProperty("extra").GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.False(properties.GetProperty("either").GetProperty("anyOf")[0].GetProperty("additionalProperties").GetBoolean());
        Assert.False(formatted.GetProperty("$defs").GetProperty("Shared").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void FormatSchema_TreatsAPropertyNamedLikeAKeywordAsAProperty()
    {
        JsonElement schema = JsonDocument.Parse("""{ "type": "object", "properties": { "properties": { "type": "object", "properties": { "a": { "type": "string" } } }, "type": { "type": "string" } } }""").RootElement;

        JsonElement formatted = JsonDocument.Parse(engine.FormatSchema(schema)).RootElement;

        Assert.Equal(["properties", "type"], formatted.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.False(formatted.GetProperty("properties").GetProperty("properties").GetProperty("additionalProperties").GetBoolean());
        Assert.False(formatted.GetProperty("properties").TryGetProperty("additionalProperties", out JsonElement _));
    }

    [Fact]
    public void FormatSchema_LeavesASchemaWithoutPropertiesAlone()
    {
        JsonElement schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        Assert.Equal("""{"type":"object"}""", engine.FormatSchema(schema));
    }

    [Fact]
    public void Flags_TellTheSessionThatTheToolIsDrivenThroughItsAppServer()
        => Assert.Equal((true, true, true), (engine.CanPersist, engine.KeepsInstructions, engine.SchemaPerTurn));

    [Fact]
    public void Model_TranslatesCapabilityToAModel()
        => Assert.Equal(["gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol"], new[] { PromptCapability.Fast, PromptCapability.Balanced, PromptCapability.Max }.Select(engine.Model));

    [Fact]
    public void Effort_TranslatesReasoningToAnEffort()
        => Assert.Equal(["low", "medium", "high", "max"], new[] { PromptReasoning.Low, PromptReasoning.Medium, PromptReasoning.High, PromptReasoning.Max }.Select(engine.Effort));

    [Fact]
    public void ModelAndEffort_RejectAnUndefinedValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Model((PromptCapability)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Effort((PromptReasoning)99));
    }

    [Fact]
    public void ThePromptsAreNotTakenAsLinesOrOneProcessPerPrompt()
    {
        Assert.Throws<NotSupportedException>(() => engine.CreateStart(Record().ToStart(null, null, null)));
        Assert.Throws<NotSupportedException>(() => engine.CreateAttachmentStart(Record().ToStart(null, null, null, [])));
        Assert.Throws<NotSupportedException>(() => engine.Frame(new PromptInput { Text = "hi", Capability = PromptCapability.Balanced, Reasoning = PromptReasoning.Medium }));
        Assert.Throws<NotSupportedException>(() => engine.Plain(new PromptInput { Text = "hi", Capability = PromptCapability.Balanced, Reasoning = PromptReasoning.Medium }));
    }

    [Fact]
    public void Attach_GivesAProcessThatTellsTheAiHowItCanWork()
    {
        using Process process = Process.Start(new ProcessStartInfo("true") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;

        Assert.IsType<CodexProcess>(engine.Attach(process, Record().ToStart(null, null, Tools())));
    }

    [Fact]
    public void Parse_ReportsTheThreadAsTheSession()
    {
        PromptEngineEvent session = Assert.Single(engine.Parse("""{"type":"thread.started","thread_id":"abc"}"""));

        Assert.Equal(PromptEngineEventKind.Session, session.Kind);
        Assert.Equal("abc", session.Text);
    }

    [Fact]
    public void Parse_ReadsAgentMessagesAsReplies()
    {
        PromptEngineEvent reply = Assert.Single(engine.Parse("""{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"hello"}}"""));

        Assert.Equal(PromptEngineEventKind.Reply, reply.Kind);
        Assert.Equal("hello", reply.Text);
    }

    [Fact]
    public void Parse_ReportsACompletedTurnAsFinished()
    {
        PromptEngineEvent finished = Assert.Single(engine.Parse("""{"type":"turn.completed"}"""));

        Assert.Equal(PromptEngineEventKind.Finished, finished.Kind);
    }

    [Fact]
    public void Parse_ReportsTheUsageOfACompletedTurn()
    {
        IReadOnlyList<PromptEngineEvent> events = engine.Parse("""{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":9,"reasoning_output_tokens":4}}""");

        Assert.Equal(new PromptUsage { InputTokens = 40, CachedInputTokens = 60, OutputTokens = 9 }, Assert.IsType<PromptUsageEvent>(events[0].Event).Usage);
        Assert.Equal(PromptEngineEventKind.Finished, events[1].Kind);
    }

    [Fact]
    public void Parse_ReadsAToolCallWhenItStartsAndItsResultWhenItEnds()
    {
        PromptToolCallEvent call = Assert.IsType<PromptToolCallEvent>(Assert.Single(engine.Parse("""{"type":"item.started","item":{"id":"item_1","type":"mcp_tool_call","server":"liveai","tool":"read_file","arguments":{"path":"a"},"result":null,"error":null,"status":"in_progress"}}""")).Event);
        PromptToolResultEvent ok = Assert.IsType<PromptToolResultEvent>(Assert.Single(engine.Parse("""{"type":"item.completed","item":{"id":"item_1","type":"mcp_tool_call","server":"liveai","tool":"read_file","arguments":{"path":"a"},"result":{"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]},"error":null,"status":"completed"}}""")).Event);
        PromptToolResultEvent failed = Assert.IsType<PromptToolResultEvent>(Assert.Single(engine.Parse("""{"type":"item.completed","item":{"id":"item_2","type":"mcp_tool_call","server":"s","tool":"t","arguments":null,"result":null,"error":{"message":"boom"},"status":"failed"}}""")).Event);

        Assert.Equal(("item_1", "liveai", "read_file", """{"path":"a"}"""), (call.Id, call.Server, call.Name, call.Input));
        Assert.Equal(("item_1", "a\nb", false), (ok.Id, ok.Output, ok.IsError));
        Assert.Equal(("item_2", "boom", true), (failed.Id, failed.Output, failed.IsError));
    }

    [Theory]
    [InlineData("""{"type":"turn.failed","error":{"message":"plain"}}""", "plain")]
    [InlineData("""{"type":"turn.failed","error":{"message":"{\"error\":{\"message\":\"wrapped\"}}"}}""", "wrapped")]
    [InlineData("""{"type":"turn.failed","error":{"message":"{\"other\":1}"}}""", "{\"other\":1}")]
    [InlineData("""{"type":"turn.failed"}""", "Codex failed.")]
    [InlineData("""{"type":"turn.failed","error":{"message":"429"}}""", "429")]
    [InlineData("""{"type":"turn.failed","error":{"message":"{\"error\":\"plain\"}"}}""", "{\"error\":\"plain\"}")]
    [InlineData("""{"type":"turn.failed","error":"boom"}""", "Codex failed.")]
    public void Parse_ReportsAFailedTurnWithItsMessage(string line, string text)
    {
        PromptEngineEvent failure = Assert.Single(engine.Parse(line));

        Assert.Equal(PromptEngineEventKind.Failed, failure.Kind);
        Assert.Equal(text, failure.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"no":"type"}""")]
    [InlineData("""{"type":"turn.started"}""")]
    [InlineData("""{"type":"error","message":"x"}""")]
    [InlineData("""{"type":"item.completed","item":{"type":"command_execution","command":"ls"}}""")]
    [InlineData("""{"type":"item.completed","item":{"type":"agent_message","text":"  "}}""")]
    [InlineData("""{"type":"item.completed"}""")]
    public void Parse_IgnoresLinesOfNoInterest(string line)
        => Assert.Empty(engine.Parse(line));

    [Fact]
    public void Accepts_ImagesOnlyAndNeedsFiles()
    {
        Assert.All(new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }, type => Assert.True(engine.Accepts(type)));
        Assert.All(new[] { "application/pdf", "text/plain", "application/zip" }, type => Assert.False(engine.Accepts(type)));
        Assert.True(engine.NeedsFiles);
    }

    private ToolEndpoint Tools()
        => new() { Url = "http://127.0.0.1:5555/mcp", Token = "secret-token", Names = ["alpha", "beta"] };
}
