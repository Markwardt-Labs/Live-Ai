namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ClaudeEngineTests
{
    private readonly ClaudeEngine engine = new("claude");

    private PromptSessionRecord Record(Action<PromptSessionRecordBuilder>? change = null)
    {
        PromptSessionRecordBuilder builder = new();
        change?.Invoke(builder);
        return builder.Build(engine);
    }

    [Fact]
    public void CreateStart_DefaultsToAReadOnlyBalancedHighEffortSession()
    {
        ProcessStartInfo start = engine.CreateStart(Record().ToStart(null, null, null));

        Assert.Equal("claude", start.FileName);
        Assert.Equal("/work", start.WorkingDirectory);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "sonnet", "--effort", "high", "--strict-mcp-config", "--append-system-prompt", Guidance(null), "--permission-mode", "dontAsk", "--tools", ""], start.ArgumentList);
    }

    [Fact]
    public void CreateStart_RunsTheModelOfTheRecordAtTheEffortItIsGiven()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder =>
        {
            builder.Model = new PromptModel { Id = "opus" };
            builder.ModelSettings = new Dictionary<string, string> { ["effort"] = "max" };
        }).ToStart(null, null, null));

        Assert.Equal("opus", start.ArgumentList[start.ArgumentList.IndexOf("--model") + 1]);
        Assert.Equal("max", start.ArgumentList[start.ArgumentList.IndexOf("--effort") + 1]);
    }

    [Fact]
    public void CreateStart_LeavesOutTheFlagsOfWhatIsNotChosen()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder =>
        {
            builder.Model = null;
            builder.ModelSettings = new Dictionary<string, string>();
        }).ToStart(null, null, null));

        Assert.DoesNotContain("--model", start.ArgumentList);
        Assert.DoesNotContain("--effort", start.ArgumentList);
    }

    [Fact]
    public async Task CreateTestStart_RunsTheCheapestModelAtTheLowestEffort()
    {
        ProcessStartInfo start = await engine.CreateTestStart(null);

        Assert.Equal("haiku", start.ArgumentList[start.ArgumentList.IndexOf("--model") + 1]);
        Assert.Equal("low", start.ArgumentList[start.ArgumentList.IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task CreateTestStart_PassesTheApiKeyAndTheConfigFolder()
    {
        ProcessStartInfo start = await new ClaudeEngine("claude", "/state").CreateTestStart("key");

        Assert.Equal("key", start.Environment["ANTHROPIC_API_KEY"]);
        Assert.Equal("/state", start.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void CreateStart_WhenSearchIsAllowed_OffersOnlyTheEnginesSearchBesideTheLibrarysServerAndAllowsIt()
    {
        PromptEngineServer server = new() { Name = "liveai", Url = "http://127.0.0.1:1/mcp", Tools = ["read_file"] };
        PromptEngineStart given = Record(builder => builder.Model = new PromptModel { Id = "m" }).ToStart(null, null, null) with { Search = true, Servers = [server] };

        ProcessStartInfo start = engine.CreateStart(given);

        Assert.Equal("WebSearch", start.ArgumentList[start.ArgumentList.IndexOf("--tools") + 1]);
        Assert.Equal("mcp__liveai__read_file,WebSearch", start.ArgumentList[start.ArgumentList.IndexOf("--allowedTools") + 1]);
        Assert.Equal("dontAsk", start.ArgumentList[start.ArgumentList.IndexOf("--permission-mode") + 1]);
    }

    [Fact]
    public void CreateStart_WhenSearchIsAllowedWithoutAnyServer_StillAllowsIt()
    {
        ProcessStartInfo start = engine.CreateStart(Record().ToStart(null, null, null) with { Search = true });

        Assert.Equal("WebSearch", start.ArgumentList[start.ArgumentList.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("--mcp-config", start.ArgumentList);
    }

    [Fact]
    public void CreateStart_WhenSearchIsNotAllowed_OffersNoNativeToolAtAll()
    {
        ProcessStartInfo start = engine.CreateStart(Record().ToStart(null, null, null));

        Assert.Equal("", start.ArgumentList[start.ArgumentList.IndexOf("--tools") + 1]);
        Assert.DoesNotContain("--allowedTools", start.ArgumentList);
    }

    [Fact]
    public void TheEngineHasASearchOfItsOwn()
        => Assert.True(((IPromptEngine)engine).NativeSearch);

    [Fact]
    public void CreateStart_ResumesTheClaudeSessionOnceItIsKnown()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder => builder.AiSessionId = "abc").ToStart(null, null, null));

        Assert.Equal("abc", start.ArgumentList[start.ArgumentList.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--resume", engine.CreateStart(Record().ToStart(null, null, null)).ArgumentList);
        Assert.DoesNotContain("--system-prompt-snapshot", engine.CreateStart(Record().ToStart(null, null, null)).ArgumentList);
    }

    [Fact]
    public void CreateStart_PassesTheApiKeyInTheEnvironment()
    {
        ProcessStartInfo start = engine.CreateStart(Record().ToStart("key", null, null));

        Assert.Equal("key", start.Environment["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("key", start.ArgumentList);
    }

    [Fact]
    public void CreateStart_PassesAChosenConfigFolderInTheEnvironment()
        => Assert.Equal("/state", new ClaudeEngine("claude", "/state").CreateStart(Record().ToStart(null, null, null)).Environment["CLAUDE_CONFIG_DIR"]);

    [Fact]
    public void CreateStart_HasNoNativeToolsAndNeverBypassesPermissions()
    {
        PromptSessionRecordBuilder builder = new();

        foreach (ProcessStartInfo start in new[] { engine.CreateStart(builder.Build(engine).ToStart(null, null, null)), engine.CreatePersistentStart(builder.Build(engine).ToStart(null, null, null)) })
        {
            Assert.Equal(string.Empty, start.ArgumentList[start.ArgumentList.IndexOf("--tools") + 1]);
            Assert.Equal("dontAsk", start.ArgumentList[start.ArgumentList.IndexOf("--permission-mode") + 1]);
            Assert.DoesNotContain("--dangerously-skip-permissions", start.ArgumentList);
        }
    }

    [Fact]
    public void CreateStart_NeverLoadsTheUsersOwnMcpServers()
    {
        PromptSessionRecordBuilder builder = new();

        Assert.Contains("--strict-mcp-config", engine.CreateStart(builder.Build(engine).ToStart(null, null, null)).ArgumentList);
        Assert.Contains("--strict-mcp-config", engine.CreatePersistentStart(builder.Build(engine).ToStart(null, null, Tools())).ArgumentList);
    }

    [Fact]
    public void CreatePersistentStart_AddsStreamInputToTheSameArguments()
    {
        PromptSessionRecord record = Record(builder => builder.AiSessionId = "abc");

        ProcessStartInfo once = engine.CreateStart(record.ToStart("key", null, null));
        ProcessStartInfo persistent = engine.CreatePersistentStart(record.ToStart("key", null, null));

        Assert.True(engine.CanPersist);
        Assert.Equal(["--input-format", "stream-json", "--replay-user-messages"], persistent.ArgumentList.Skip(1).Take(3));
        Assert.Equal(once.ArgumentList, persistent.ArgumentList.Where((argument, index) => index is < 1 or > 3));
        Assert.Equal("key", persistent.Environment["ANTHROPIC_API_KEY"]);
        Assert.Equal("/work", persistent.WorkingDirectory);
    }

    [Fact]
    public void CreateStart_WithASchema_PassesItInline()
    {
        string schema = """{"type":"object"}""";

        ProcessStartInfo once = engine.CreateStart(Record().ToStart(null, schema, null));
        ProcessStartInfo persistent = engine.CreatePersistentStart(Record().ToStart(null, schema, null));

        Assert.Equal(schema, once.ArgumentList[once.ArgumentList.IndexOf("--json-schema") + 1]);
        Assert.Equal(schema, persistent.ArgumentList[persistent.ArgumentList.IndexOf("--json-schema") + 1]);
        Assert.DoesNotContain("--json-schema", engine.CreateStart(Record().ToStart(null, null, null)).ArgumentList);
    }

    [Fact]
    public void CreateStart_WithExternalServers_ConfiguresThemAllowsTheirToolsAndKeepsTheLibraryServer()
    {
        PromptSessionRecord record = Record(builder => builder.McpServers = new Dictionary<string, PromptMcpServer>
        {
            ["files"] = PromptMcpServer.Stdio("npx", "-y", "server") with { EnvironmentVariables = new Dictionary<string, string> { ["KEY"] = "value" } },
            ["web"] = PromptMcpServer.Http("https://example.com/mcp", "WEB_TOKEN"),
            ["open"] = PromptMcpServer.Http("https://example.com/open")
        });

        ProcessStartInfo start = engine.CreateStart(record.ToStart(null, null, Tools()));

        JsonElement servers = JsonDocument.Parse(start.ArgumentList[start.ArgumentList.IndexOf("--mcp-config") + 1]).RootElement.GetProperty("mcpServers");
        Assert.Equal(["liveai", "files", "web", "open"], servers.EnumerateObject().Select(server => server.Name));
        JsonElement files = servers.GetProperty("files");
        Assert.Equal("stdio", files.GetProperty("type").GetString());
        Assert.Equal("npx", files.GetProperty("command").GetString());
        Assert.Equal(["-y", "server"], files.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()));
        Assert.Equal("value", files.GetProperty("env").GetProperty("KEY").GetString());
        Assert.Equal("Bearer ${WEB_TOKEN}", servers.GetProperty("web").GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.False(servers.GetProperty("open").TryGetProperty("headers", out _));
        Assert.Equal("mcp__liveai__alpha,mcp__liveai__beta,mcp__files,mcp__web,mcp__open", start.ArgumentList[start.ArgumentList.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("secret-token", string.Join(' ', start.ArgumentList));
    }

    [Fact]
    public void CreateStart_WithOnlyExternalServers_StillConfiguresThem()
    {
        PromptSessionRecord record = Record(builder => builder.McpServers = new Dictionary<string, PromptMcpServer> { ["db"] = PromptMcpServer.Http("https://example.com/mcp") });

        ProcessStartInfo start = engine.CreateStart(record.ToStart(null, null, null));

        Assert.Equal("mcp__db", start.ArgumentList[start.ArgumentList.IndexOf("--allowedTools") + 1]);
        Assert.Equal(["db"], JsonDocument.Parse(start.ArgumentList[start.ArgumentList.IndexOf("--mcp-config") + 1]).RootElement.GetProperty("mcpServers").EnumerateObject().Select(server => server.Name));
        Assert.Contains("MCP servers named db", start.ArgumentList[start.ArgumentList.IndexOf("--append-system-prompt") + 1]);
    }

    [Fact]
    public void CreateStart_WithInstructions_AppendsThemToTheSystemPrompt()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder => builder.Instructions = "Be \"brief\".\nThanks.").ToStart(null, null, null));

        Assert.EndsWith("Instructions from the user:\nBe \"brief\".\nThanks.", start.ArgumentList[start.ArgumentList.IndexOf("--append-system-prompt") + 1]);
    }

    [Fact]
    public void CreateStart_ForABranchThatHasNoSessionYet_ResumesTheOriginalAsACopy()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder => builder.ForkedFrom = "origin-id").ToStart(null, null, null));
        ProcessStartInfo persistent = engine.CreatePersistentStart(Record(builder => builder.ForkedFrom = "origin-id").ToStart(null, null, null));

        Assert.Equal(["--resume", "origin-id", "--fork-session", "--system-prompt-snapshot", "off"], start.ArgumentList.TakeLast(5));
        Assert.Equal(["--resume", "origin-id", "--fork-session", "--system-prompt-snapshot", "off"], persistent.ArgumentList.TakeLast(5));
    }

    [Fact]
    public void CreateStart_ForABranchWithItsOwnSession_ResumesThatWithoutCopying()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder => { builder.ForkedFrom = "origin-id"; builder.AiSessionId = "own-id"; }).ToStart(null, null, null));

        Assert.Equal(["--resume", "own-id", "--system-prompt-snapshot", "off"], start.ArgumentList.TakeLast(4));
        Assert.DoesNotContain("--fork-session", start.ArgumentList);
    }

    [Fact]
    public void CreateStart_WhenResuming_RendersTheInstructionsAgainSoAChangeReachesTheConversation()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder => builder.AiSessionId = "abc").ToStart(null, null, null));

        Assert.Equal("off", start.ArgumentList[start.ArgumentList.IndexOf("--system-prompt-snapshot") + 1]);
    }

    [Fact]
    public void Flags_TellTheSessionWhatTheToolNeeds()
        => Assert.Equal((false, false), (engine.KeepsInstructions, engine.SchemaPerTurn));

    [Fact]
    public async Task Branch_OfTheWholeConversation_LeavesTheCopyToTheToolAtItsFirstPrompt()
    {
        PromptBranch branch = await engine.Branch(Record(builder => builder.AiSessionId = "origin").ToStart(null, null, null), null);

        Assert.Equal(new PromptBranch { ForkedFrom = "origin" }, branch);
    }

    [Fact]
    public async Task Branch_OfABranchThatIsNotPromptedYet_KeepsItsOriginAndItsPoint()
    {
        PromptBranch branch = await engine.Branch(Record(builder => { builder.ForkedFrom = "origin"; builder.ForkedAt = "message"; }).ToStart(null, null, null), null);

        Assert.Equal(new PromptBranch { ForkedFrom = "origin", ForkedAt = "message" }, branch);
    }

    [Fact]
    public async Task Branch_OfASessionWithoutAConversation_HasNothingToBranch()
        => Assert.Equal(new PromptBranch(), await engine.Branch(Record().ToStart(null, null, null), null));

    [Fact]
    public void CreateStart_ForABranchAtAPoint_ResumesTheOriginalAtThatMessageAsACopy()
    {
        ProcessStartInfo start = engine.CreateStart(Record(builder => { builder.ForkedFrom = "origin-id"; builder.ForkedAt = "message-id"; }).ToStart(null, null, null));

        Assert.Equal(["--resume", "origin-id", "--resume-session-at", "message-id", "--fork-session", "--system-prompt-snapshot", "off"], start.ArgumentList.TakeLast(7));
    }

    [Fact]
    public void CreateStart_AsksForTheTextAsItIsWritten()
        => Assert.Contains("--include-partial-messages", engine.CreateStart(Record().ToStart(null, null, null)).ArgumentList);

    [Fact]
    public void Parse_TurnsTheTextThatIsBeingWrittenIntoDeltas()
    {
        PromptEngineEvent delta = Assert.Single(engine.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hel"}}}"""));

        Assert.Equal(new PromptDeltaEvent { Text = "hel" }, delta.Event);
    }

    [Theory]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_start","index":0}}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{"}}}""")]
    [InlineData("""{"type":"stream_event","parent_tool_use_id":"toolu_1","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"sub"}}}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":""}}}""")]
    public void Parse_IgnoresStreamEventsThatAreNotTextOfTheMainAi(string line)
        => Assert.Empty(engine.Parse(line));

    [Fact]
    public void Frame_WithNotes_PutsEachInFrontOfThePromptAsASystemReminder()
    {
        string line = engine.Frame(Turn("go", notes: ["the build passes", "a file changed"]));

        JsonElement content = JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Contains("<system-reminder>", content[0].GetProperty("text").GetString());
        Assert.Contains("the build passes", content[0].GetProperty("text").GetString());
        Assert.Contains("a file changed", content[1].GetProperty("text").GetString());
        Assert.Equal("go", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public void Plain_IsTheTextAloneOrAfterItsNotes()
    {
        Assert.Equal("go", engine.Plain(Turn("go")));
        string withNote = engine.Plain(Turn("go", notes: ["careful"]));
        Assert.StartsWith("<system-reminder>", withNote);
        Assert.Contains("careful", withNote);
        Assert.EndsWith("\n\ngo", withNote);
    }

    [Fact]
    public void Attach_GivesAProcessThatTakesPromptsAndTheOtherOperations()
    {
        using Process process = Process.Start(new ProcessStartInfo("true") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;

        Assert.IsType<ClaudeProcess>(engine.Attach(process, Record().ToStart(null, null, null)));
    }

    private PromptInput Turn(string text, IReadOnlyList<string>? notes = null, IReadOnlyList<PromptAttachment>? attachments = null)
        => new() { Text = text, Model = "sonnet", Notes = notes ?? [], Attachments = attachments ?? [] };

    private string Guidance(ToolEndpoint? tools)
        => tools.Guidance(Record());

    private ToolEndpoint Tools()
        => new() { Url = "http://127.0.0.1:5555/mcp", Token = "secret-token", Names = ["alpha", "beta"], CacheName = "CACHE:" };

    [Fact]
    public void CreateStart_WithTools_PassesAnMcpConfigAndAllowsTheTools()
    {
        ProcessStartInfo start = engine.CreateStart(Record().ToStart(null, null, Tools()));

        string config = start.ArgumentList[start.ArgumentList.IndexOf("--mcp-config") + 1];
        using JsonDocument document = JsonDocument.Parse(config);
        JsonElement server = document.RootElement.GetProperty("mcpServers").GetProperty("liveai");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:5555/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer ${LIVEAI_TOOLS_TOKEN}", server.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("mcp__liveai__alpha,mcp__liveai__beta", start.ArgumentList[start.ArgumentList.IndexOf("--allowedTools") + 1]);
    }

    [Fact]
    public void CreateStart_WithTools_KeepsTheSecretOffTheCommandLine()
    {
        ProcessStartInfo once = engine.CreateStart(Record().ToStart(null, null, Tools()));
        ProcessStartInfo persistent = engine.CreatePersistentStart(Record().ToStart(null, null, Tools()));

        Assert.Equal("secret-token", once.Environment["LIVEAI_TOOLS_TOKEN"]);
        Assert.Equal("secret-token", persistent.Environment["LIVEAI_TOOLS_TOKEN"]);
        Assert.DoesNotContain(once.ArgumentList, argument => argument.Contains("secret-token"));
        Assert.Contains("--mcp-config", persistent.ArgumentList);
    }

    [Fact]
    public void CreateStart_TellsTheAiHowItCanWorkInAnyCaseOfTools()
    {
        foreach ((ToolEndpoint? tools, string expected) in new[] { ((ToolEndpoint?)null, "You have no tools"), (Tools(), "file tools of the MCP server named liveai") })
        {
            ProcessStartInfo once = engine.CreateStart(Record().ToStart(null, null, tools));
            ProcessStartInfo persistent = engine.CreatePersistentStart(Record().ToStart(null, null, tools));

            Assert.Contains(expected, once.ArgumentList[once.ArgumentList.IndexOf("--append-system-prompt") + 1]);
            Assert.Equal(tools.Guidance(Record()), persistent.ArgumentList[persistent.ArgumentList.IndexOf("--append-system-prompt") + 1]);
        }
    }

    [Fact]
    public void CreateStart_WithoutTools_PassesNoMcpConfig()
    {
        ProcessStartInfo start = engine.CreateStart(Record().ToStart(null, null, null));

        Assert.DoesNotContain("--mcp-config", start.ArgumentList);
        Assert.DoesNotContain("--allowedTools", start.ArgumentList);
    }

    [Fact]
    public void FormatSchema_KeepsTheSchemaAsItIs()
    {
        JsonElement schema = JsonDocument.Parse("""{ "type": "object", "properties": { "a": { "type": "string" } } }""").RootElement;

        Assert.Equal("""{ "type": "object", "properties": { "a": { "type": "string" } } }""", engine.FormatSchema(schema));
    }

    [Fact]
    public void Parse_ReportsAStructuredResultAsTheLastReply()
    {
        IReadOnlyList<PromptEngineEvent> events = engine.Parse("""{"type":"result","is_error":false,"session_id":"abc","result":"{\"a\":1}","structured_output":{"a":1,"b":[true]}}""");

        Assert.Equal([PromptEngineEventKind.Session, PromptEngineEventKind.Reply, PromptEngineEventKind.Finished], events.Select(output => output.Kind));
        Assert.Equal("""{"a":1,"b":[true]}""", events[1].Text);
    }

    [Fact]
    public void Parse_IgnoresAStructuredOutputThatIsNotAnObject()
    {
        IReadOnlyList<PromptEngineEvent> events = engine.Parse("""{"type":"result","is_error":false,"structured_output":null}""");

        Assert.Equal([PromptEngineEventKind.Finished], events.Select(output => output.Kind));
    }

    [Fact]
    public void Frame_WrapsThePromptAsOneUserMessageLine()
    {
        string line = engine.Frame(Turn("say \"hi\"\nplease"));

        Assert.DoesNotContain('\n', line);
        using JsonDocument document = JsonDocument.Parse(line);
        Assert.Equal("user", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("user", document.RootElement.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("say \"hi\"\nplease", document.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Parse_ReadsTheTextOfAnAssistantMessageAndItsToolCallsInOrder()
    {
        IReadOnlyList<PromptEngineEvent> events = engine.Parse("""{"type":"assistant","message":{"content":[{"type":"thinking","text":"hmm"},{"type":"text","text":"one"},{"type":"text","text":"and"},{"type":"tool_use","id":"toolu_1","name":"mcp__liveai__read_file","input":{"path":"a/b"}},{"type":"text","text":"two"}]}}""");

        Assert.Equal(3, events.Count);
        Assert.Equal(PromptEngineEventKind.Reply, events[0].Kind);
        Assert.Equal("one\n\nand", events[0].Text);
        PromptToolCallEvent call = Assert.IsType<PromptToolCallEvent>(events[1].Event);
        Assert.Equal(PromptEngineEventKind.Event, events[1].Kind);
        Assert.Equal(("toolu_1", "liveai", "read_file", """{"path":"a/b"}"""), (call.Id, call.Server, call.Name, call.Input));
        Assert.Equal("two", events[2].Text);
    }

    [Fact]
    public void InterruptFrame_IsAControlRequestAndTheResponseToItIsIgnored()
    {
        JsonElement frame = JsonDocument.Parse(engine.InterruptFrame!).RootElement;

        Assert.Equal("control_request", frame.GetProperty("type").GetString());
        Assert.Equal("interrupt", frame.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Empty(engine.Parse("""{"type":"control_response","response":{"subtype":"success","request_id":"liveai-interrupt"}}"""));
    }

    [Fact]
    public void Parse_ReadsAToolWithoutAServerAsItIs()
    {
        PromptToolCallEvent call = Assert.IsType<PromptToolCallEvent>(Assert.Single(engine.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Read","input":{}}]}}""")).Event);

        Assert.Null(call.Server);
        Assert.Equal("Read", call.Name);
    }

    [Fact]
    public void Parse_ReadsToolResultsOfUserMessagesAsTextOrBlocks()
    {
        PromptToolResultEvent text = Assert.IsType<PromptToolResultEvent>(Assert.Single(engine.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"a","content":"plain","is_error":true}]}}""")).Event);
        PromptToolResultEvent blocks = Assert.IsType<PromptToolResultEvent>(Assert.Single(engine.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"b","content":[{"type":"text","text":"x"},{"type":"text","text":"y"}]}]}}""")).Event);

        Assert.Equal(("a", "plain", true), (text.Id, text.Output, text.IsError));
        Assert.Equal(("b", "x\ny", false), (blocks.Id, blocks.Output, blocks.IsError));
        Assert.Empty(engine.Parse("""{"type":"user","message":{"content":"just text"}}"""));
        Assert.Empty(engine.Parse("""{"type":"user","parent_tool_use_id":"p","message":{"content":[{"type":"tool_result","tool_use_id":"c","content":"inner"}]}}"""));
    }

    [Fact]
    public void Parse_ReadsTheWindowsFromARateLimitEvent()
    {
        IReadOnlyList<PromptWindow> windows = Assert.IsType<PromptWindowsEvent>(Assert.Single(engine.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"five_hour":{"utilization":0.25,"resetsAt":1793058600},"seven_day":{"utilization":0.5,"resetsAt":1793430000}}}}""")).Event).Windows;

        Assert.Equal(
            [
                new PromptWindow { Name = "Session", Used = 0.25, Length = TimeSpan.FromHours(5), ReadAt = windows[0].ReadAt, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1793058600) },
                new PromptWindow { Name = "Week", Used = 0.5, Length = TimeSpan.FromDays(7), ReadAt = windows[0].ReadAt, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1793430000) }
            ],
            windows);
    }

    [Fact]
    public void Parse_ReadsAMissingWindowAsNothingAndIgnoresARateLimitEventWithoutWindows()
    {
        IReadOnlyList<PromptWindow> windows = Assert.IsType<PromptWindowsEvent>(Assert.Single(engine.Parse("""{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.1,"resetsAt":1793058600}}}}""")).Event).Windows;

        PromptWindow session = Assert.Single(windows);
        Assert.Equal(("Session", 0.1), (session.Name, session.Used));
        Assert.Empty(engine.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}"""));
    }

    [Fact]
    public void Parse_ReportsTheUsageOfAFinishedPrompt()
    {
        IReadOnlyList<PromptEngineEvent> events = engine.Parse("""{"type":"result","session_id":"s","total_cost_usd":0.5,"usage":{"input_tokens":2,"cache_creation_input_tokens":8,"cache_read_input_tokens":30,"output_tokens":7}}""");

        PromptUsage usage = Assert.IsType<PromptUsageEvent>(events.Single(output => output.Kind == PromptEngineEventKind.Event).Event).Usage;
        Assert.Equal(new PromptUsage { InputTokens = 10, CachedInputTokens = 30, OutputTokens = 7, CostUsd = 0.5m }, usage);
        Assert.Equal(PromptEngineEventKind.Finished, events[^1].Kind);
    }

    [Fact]
    public void Parse_IgnoresMessagesWithoutTextAndMessagesOfSubagents()
    {
        Assert.Empty(engine.Parse("""{"type":"assistant","message":{"content":[{"type":"thinking","text":"hmm"}]}}"""));
        Assert.Empty(engine.Parse("""{"type":"assistant","parent_tool_use_id":"toolu_1","message":{"content":[{"type":"text","text":"inner"}]}}"""));
    }

    [Fact]
    public void Parse_ReportsTheSessionAndTheEndOfASuccessfulResult()
    {
        IReadOnlyList<PromptEngineEvent> events = engine.Parse("""{"type":"result","is_error":false,"session_id":"abc"}""");

        Assert.Equal([PromptEngineEventKind.Session, PromptEngineEventKind.Finished], events.Select(output => output.Kind));
        Assert.Equal("abc", events[0].Text);
    }

    [Fact]
    public void Parse_ReportsOnlyTheEndOfAResultWithoutASession()
    {
        PromptEngineEvent finished = Assert.Single(engine.Parse("""{"type":"result","is_error":false}"""));

        Assert.Equal(PromptEngineEventKind.Finished, finished.Kind);
    }

    [Theory]
    [InlineData("""{"type":"result","is_error":true,"result":"Invalid API key","session_id":"abc"}""", "Invalid API key")]
    [InlineData("""{"type":"result","is_error":true,"errors":["first","second"]}""", "first\nsecond")]
    [InlineData("""{"type":"result","is_error":true}""", "Claude Code failed.")]
    public void Parse_ReportsAFailedResultWithoutRecordingItsSession(string line, string text)
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
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"assistant"}""")]
    public void Parse_IgnoresLinesOfNoInterest(string line)
        => Assert.Empty(engine.Parse(line));

    [Fact]
    public void Accepts_ImagesAndPdfsOnly()
    {
        Assert.All(new[] { "image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf" }, type => Assert.True(engine.Accepts(type)));
        Assert.All(new[] { "text/plain", "image/bmp", "application/zip" }, type => Assert.False(engine.Accepts(type)));
        Assert.False(engine.NeedsFiles);
    }

    [Fact]
    public void Frame_WithAttachments_IsOneUserMessageWithTheFilesBeforeTheText()
    {
        PromptAttachment image = new() { Name = "a.png", MediaType = "image/png", Content = new byte[] { 1, 2, 3 } };
        PromptAttachment pdf = new() { Name = "a.pdf", MediaType = "application/pdf", Content = new byte[] { 4, 5 } };

        string frame = engine.Frame(Turn("what is this?", attachments: [image, pdf]));

        Assert.DoesNotContain('\n', frame);
        JsonElement message = JsonDocument.Parse(frame).RootElement;
        Assert.Equal("user", message.GetProperty("type").GetString());
        JsonElement content = message.GetProperty("message").GetProperty("content");
        Assert.Equal(["image", "document", "text"], content.EnumerateArray().Select(block => block.GetProperty("type").GetString()));
        Assert.Equal("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("base64", content[0].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("application/pdf", content[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("what is this?", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public void CreateAttachmentStart_ReadsStreamInputLikeThePersistentStartAndIgnoresFiles()
    {
        PromptSessionRecord record = Record();

        ProcessStartInfo start = engine.CreateAttachmentStart(record.ToStart("key", null, null, ["/tmp/ignored.png"]));

        Assert.Equal(engine.CreatePersistentStart(record.ToStart("key", null, null)).ArgumentList, start.ArgumentList);
        Assert.Equal("stream-json", start.ArgumentList[start.ArgumentList.IndexOf("--input-format") + 1]);
        Assert.DoesNotContain("/tmp/ignored.png", start.ArgumentList);
        Assert.Equal("key", start.Environment["ANTHROPIC_API_KEY"]);
    }
}
