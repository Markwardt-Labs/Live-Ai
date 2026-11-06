namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class EngineFilesTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private string Write(string relative, params string[] lines)
    {
        string path = temp.Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public async Task Claude_ReadsTheHistoryWithPromptsRepliesAndToolsAndSkipsTheRest()
    {
        Write("projects/-p/abc.jsonl",
            """{"type":"queue-operation","operation":"enqueue"}""",
            """{"type":"user","timestamp":"2026-01-02T03:04:05.000Z","message":{"role":"user","content":"hello"}}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"meta"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"hi"},{"type":"tool_use","id":"t1","name":"mcp__liveai__read_file","input":{"path":"a"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"file text"}]}]}}""",
            """{"type":"assistant","isSidechain":true,"message":{"role":"assistant","content":[{"type":"text","text":"inner"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"image","source":{}},{"type":"text","text":"look"}]}}""",
            "not json at all",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"  "}]}}""");

        List<PromptHistoryEntry> newest = await new ClaudeEngine("claude", temp.Path).ReadHistory("abc").ToListAsync();
        List<PromptHistoryEntry> entries = [.. newest.AsEnumerable().Reverse()];

        Assert.Equal([typeof(PromptInputEvent), typeof(PromptMessageEvent), typeof(PromptToolCallEvent), typeof(PromptToolResultEvent), typeof(PromptInputEvent), typeof(PromptInputEvent)], entries.Select(entry => entry.Event.GetType()));
        Assert.Equal(["hello", "hi", """{"path":"a"}""", "file text", "[image]", "look"], entries.Select(entry => entry.Text));
        PromptToolCallEvent call = Assert.IsType<PromptToolCallEvent>(entries[2].Event);
        Assert.Equal("read_file", call.Name);
        Assert.False(string.IsNullOrEmpty(call.Id));
        Assert.Equal(call.Id, Assert.IsType<PromptToolResultEvent>(entries[3].Event).Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), entries[0].Time);
        Assert.Null(entries[1].Time);
    }

    [Fact]
    public async Task Claude_History_IsReadNewestFirstWithThePositionOfEachRecord()
    {
        string first = """{"type":"user","message":{"role":"user","content":"one"}}""";
        string second = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"two"},{"type":"tool_use","id":"t","name":"x","input":{}}]}}""";
        string third = """{"type":"user","message":{"role":"user","content":"three"}}""";
        Write("projects/-p/abc.jsonl", first, second, third);

        List<PromptHistoryEntry> entries = await new ClaudeEngine("claude", temp.Path).ReadHistory("abc").ToListAsync();

        Assert.Equal(["three", "{}", "two", "one"], entries.Select(entry => entry.Text));
        Assert.Equal([0, 1, 0, 0], entries.Select(entry => entry.Point.Index));
        Assert.Equal([first.Length + second.Length + 2, first.Length + 1, first.Length + 1, 0], entries.Select(entry => entry.Point.Position));
    }

    [Fact]
    public async Task Claude_History_IsReadLazilyFromTheEnd()
    {
        string path = Write("projects/-p/abc.jsonl", Enumerable.Range(0, 5000).Select(number => $$$"""{"type":"user","message":{"role":"user","content":"line {{{number}}} {{{new string('x', 50)}}}"}}""").ToArray());

        PromptHistoryEntry newest = await new ClaudeEngine("claude", temp.Path).ReadHistory("abc").FirstAsync();

        Assert.StartsWith("line 4999 ", newest.Text);
        using FileStream stream = File.OpenRead(path);
        stream.Seek(newest.Point.Position, SeekOrigin.Begin);
        Assert.StartsWith("{\"type\":\"user\"", new StreamReader(stream).ReadLine());
    }

    [Fact]
    public async Task Claude_WithoutAStoredConversation_HasAnEmptyHistory()
    {
        Assert.Empty(await new ClaudeEngine("claude", temp.Path).ReadHistory("abc").ToListAsync());
        Assert.Empty(await new ClaudeEngine("claude", temp.Combine("missing")).ReadHistory("abc").ToListAsync());
    }

    [Fact]
    public async Task Codex_ReadsTheHistoryWithPromptsRepliesAndMcpToolCalls()
    {
        Write("sessions/2026/01/01/rollout-2026-01-01T00-00-00-abc.jsonl",
            """{"timestamp":"2026-01-02T03:04:05Z","type":"session_meta","payload":{"id":"abc","session_id":"abc"}}""",
            """{"timestamp":"2026-01-02T03:04:06Z","type":"event_msg","payload":{"type":"user_message","message":"hello"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[]}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"hi"}}""",
            """{"type":"event_msg","payload":{"type":"mcp_tool_call_end","invocation":{"server":"liveai","tool":"read_file","arguments":{"path":"a"}},"result":{"Ok":{"content":[{"type":"text","text":"file"},{"type":"text","text":"text"}],"isError":false}}}}""",
            """{"type":"event_msg","payload":{"type":"mcp_tool_call_end","invocation":{"server":"liveai","tool":"shell"},"result":{"Err":"boom"}}}""",
            "{broken",
            """{"type":"event_msg","payload":{"type":"token_count"}}""");

        List<PromptHistoryEntry> newest = await new CodexEngine("codex", temp.Path).ReadHistory("abc").ToListAsync();
        List<PromptHistoryEntry> entries = [.. newest.AsEnumerable().Reverse()];

        Assert.Equal([typeof(PromptInputEvent), typeof(PromptMessageEvent), typeof(PromptToolCallEvent), typeof(PromptToolResultEvent), typeof(PromptToolCallEvent), typeof(PromptToolResultEvent)], entries.Select(entry => entry.Event.GetType()));
        Assert.Equal(["hello", "hi", """{"path":"a"}""", "file\ntext", "{}", "boom"], entries.Select(entry => entry.Text));
        Assert.Equal(["read_file", "shell"], entries.Where(entry => entry.Event.GetType() == typeof(PromptToolCallEvent)).Select(entry => ((PromptToolCallEvent)entry.Event).Name));
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:06Z"), entries[0].Time);
    }

    [Fact]
    public async Task Codex_WithoutAStoredConversation_HasAnEmptyHistory()
        => Assert.Empty(await new CodexEngine("codex", temp.Path).ReadHistory("abc").ToListAsync());

    private PromptSessionRecord Record(string? aiSessionId = null, string? forkedFrom = null, string? forkedAt = null)
        => new() { Id = Guid.NewGuid(), Engine = new ClaudeEngine(), WorkingFolder = temp.Path, AiSessionId = aiSessionId, ForkedFrom = forkedFrom, ForkedAt = forkedAt };

    private string[] Codex(params string[] turns)
        => [.. new[] { """{"type":"session_meta","payload":{"id":"abc"}}""" }.Concat(turns.SelectMany((turn, index) => new[]
        {
            $$$"""{"type":"event_msg","payload":{"type":"task_started","turn_id":"{{{turn}}}"}}""",
            $$$"""{"type":"event_msg","payload":{"type":"user_message","message":"prompt {{{index}}}"}}""",
            $$$"""{"type":"event_msg","payload":{"type":"agent_message","message":"answer {{{index}}}"}}""",
            $$$"""{"type":"event_msg","payload":{"type":"task_complete","turn_id":"{{{turn}}}"}}"""
        }))];

    [Fact]
    public async Task Codex_BranchAsksTheAppServerToForkTheThreadAndReportsTheNewOne()
    {
        FakeCodex fake = new(temp);
        CodexEngine engine = new(fake.Script(), temp.Path);

        PromptBranch branch = await engine.Branch(Record("abc").ToStart("key", null, null), null);

        Assert.Equal(new PromptBranch { AiSessionId = "codex-fork-1" }, branch);
        JsonElement fork = Assert.Single(await fake.Parameters("thread/fork"));
        Assert.Equal("abc", fork.GetProperty("threadId").GetString());
        Assert.False(fork.TryGetProperty("lastTurnId", out JsonElement _));
        Assert.Equal("key", await File.ReadAllTextAsync(temp.Combine("codex-key.txt")));
        Assert.Equal(["app-server"], await File.ReadAllLinesAsync(temp.Combine("codex-args.txt")));
    }

    [Fact]
    public async Task Codex_BranchAtAnEntry_ForksThroughTheTurnThatEntryBelongsTo()
    {
        FakeCodex fake = new(temp);
        string source = Write("sessions/2026/01/01/rollout-2026-01-01T00-00-00-abc.jsonl", Codex("turn-a", "turn-b", "turn-c"));
        CodexEngine engine = new(fake.Script(), temp.Path);
        List<PromptHistoryEntry> history = await engine.ReadHistory("abc").ToListAsync();
        PromptHistoryEntry answer = history.Single(entry => entry.Text == "answer 1");
        PromptHistoryEntry prompt = history.Single(entry => entry.Text == "prompt 0");

        await engine.Branch(Record("abc").ToStart(null, null, null), answer.Point);
        await engine.Branch(Record("abc").ToStart(null, null, null), prompt.Point);

        Assert.True(File.Exists(source));
        Assert.Equal(["turn-b", "turn-a"], (await fake.Parameters("thread/fork")).Select(request => request.GetProperty("lastTurnId").GetString()));
    }

    [Fact]
    public async Task Codex_BranchOfASessionWithoutAThread_HasNothingToBranch()
        => Assert.Equal(new PromptBranch(), await new CodexEngine(new FakeCodex(temp).Script(), temp.Path).Branch(Record().ToStart(null, null, null), null));

    [Fact]
    public async Task Codex_BranchAtAnEntryOfAMissingOrTurnlessConversation_Fails()
    {
        CodexEngine engine = new(new FakeCodex(temp).Script(), temp.Path);
        Write("sessions/2026/01/01/rollout-2026-01-01T00-00-00-bare.jsonl", """{"type":"session_meta","payload":{"id":"bare"}}""", """{"type":"event_msg","payload":{"type":"user_message","message":"hi"}}""");
        PromptHistoryEntry entry = new() { Event = new PromptInputEvent { Text = "hi" } };

        await Assert.ThrowsAsync<PromptSessionException>(() => engine.Branch(Record("missing").ToStart(null, null, null), entry.Point));
        await Assert.ThrowsAsync<PromptSessionException>(() => engine.Branch(Record("bare").ToStart(null, null, null), entry.Point));
    }

    [Fact]
    public async Task Codex_BranchWhenTheAppServerRefusesOrStops_Fails()
    {
        CodexEngine refusing = new(temp.Script("refuse", "while IFS= read -r line; do case \"$line\" in *thread/fork*) echo '{\"id\":2,\"error\":{\"message\":\"no such thread\"}}';; esac; done"), temp.Path);
        CodexEngine stopping = new(temp.Script("stop", "exit 0"), temp.Path);

        PromptSessionException refused = await Assert.ThrowsAsync<PromptSessionException>(() => refusing.Branch(Record("abc").ToStart(null, null, null), null));
        PromptSessionException stopped = await Assert.ThrowsAsync<PromptSessionException>(() => stopping.Branch(Record("abc").ToStart(null, null, null), null));

        Assert.Equal("no such thread", refused.Message);
        Assert.Contains("stopped", stopped.Message);
    }

    private string TranscriptLine(string type, string uuid, string content, string extra = "")
        => $$$"""{"type":"{{{type}}}","uuid":"{{{uuid}}}","message":{"role":"{{{type}}}","content":{{{content}}}}{{{extra}}}}""";

    private async Task<(ClaudeEngine Provider, List<PromptHistoryEntry> History)> Claude(params string[] lines)
    {
        Write("projects/-work/abc.jsonl", lines);
        ClaudeEngine engine = new("claude", temp.Path);
        return (engine, await engine.ReadHistory("abc").ToListAsync());
    }

    [Fact]
    public async Task Claude_BranchAtAnEntry_ResumesAtTheLastAnswerOfTheTurnThatEntryBelongsTo()
    {
        (ClaudeEngine engine, List<PromptHistoryEntry> history) = await Claude(
            TranscriptLine("user", "u1", "\"first\""),
            TranscriptLine("assistant", "a1", """[{"type":"tool_use","id":"t1","name":"x","input":{}}]"""),
            TranscriptLine("user", "r1", """[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]"""),
            TranscriptLine("assistant", "a2", """[{"type":"text","text":"done one"}]"""),
            TranscriptLine("user", "u2", "\"second\""),
            TranscriptLine("assistant", "a3", """[{"type":"text","text":"done two"}]"""));

        PromptBranch fromPrompt = await engine.Branch(Record("abc").ToStart(null, null, null), history.Single(entry => entry.Text == "first").Point);
        PromptBranch fromAnswer = await engine.Branch(Record("abc").ToStart(null, null, null), history.Single(entry => entry.Text == "done one").Point);
        PromptBranch fromLast = await engine.Branch(Record("abc").ToStart(null, null, null), history.Single(entry => entry.Text == "second").Point);

        Assert.Equal(new PromptBranch { ForkedFrom = "abc", ForkedAt = "a2" }, fromPrompt);
        Assert.Equal(new PromptBranch { ForkedFrom = "abc", ForkedAt = "a2" }, fromAnswer);
        Assert.Equal(new PromptBranch { ForkedFrom = "abc", ForkedAt = "a3" }, fromLast);
    }

    [Fact]
    public async Task Claude_BranchAtAnEntryFromBeforeACompaction_IsRefused()
    {
        (ClaudeEngine engine, List<PromptHistoryEntry> history) = await Claude(
            TranscriptLine("user", "u1", "\"before\""),
            TranscriptLine("assistant", "a1", """[{"type":"text","text":"old"}]"""),
            """{"type":"system","subtype":"compact_boundary","uuid":"boundary"}""",
            TranscriptLine("user", "u2", "\"after\""),
            TranscriptLine("assistant", "a2", """[{"type":"text","text":"new"}]"""));

        PromptSessionException refused = await Assert.ThrowsAsync<PromptSessionException>(() => engine.Branch(Record("abc").ToStart(null, null, null), history.Single(entry => entry.Text == "before").Point));
        PromptBranch allowed = await engine.Branch(Record("abc").ToStart(null, null, null), history.Single(entry => entry.Text == "after").Point);

        Assert.Contains("compacted", refused.Message);
        Assert.Equal("a2", allowed.ForkedAt);
    }

    [Fact]
    public async Task Claude_BranchAtAnEntryOfATurnWithoutAnAnswerOrOfAMissingConversation_Fails()
    {
        (ClaudeEngine engine, List<PromptHistoryEntry> history) = await Claude(TranscriptLine("user", "u1", "\"asked\""));

        await Assert.ThrowsAsync<PromptSessionException>(() => engine.Branch(Record("abc").ToStart(null, null, null), history.Single().Point));
        await Assert.ThrowsAsync<PromptSessionException>(() => engine.Branch(Record("missing").ToStart(null, null, null), history.Single().Point));
    }

    [Fact]
    public async Task Claude_DeleteSession_RemovesTheConversationItsExtrasAndTheProjectFolderWhenEmpty()
    {
        Write("projects/-alone/abc.jsonl", "{}");
        Write("projects/-alone/abc/subagents/one.jsonl", "{}");
        Write("projects/-shared/xyz.jsonl", "{}");
        Write("projects/-shared/other.jsonl", "{}");
        ClaudeEngine engine = new("claude", temp.Path);

        await engine.DeleteSession("abc");
        await engine.DeleteSession("xyz");

        Assert.False(Directory.Exists(temp.Combine("projects/-alone")));
        Assert.False(File.Exists(temp.Combine("projects/-shared/xyz.jsonl")));
        Assert.True(File.Exists(temp.Combine("projects/-shared/other.jsonl")));
        Assert.False(engine.HasSession("abc"));
        await engine.DeleteSession("abc");
        await new ClaudeEngine("claude", temp.Combine("missing")).DeleteSession("abc");
    }

    [Fact]
    public async Task Claude_MoveSession_CopiesTheConversationAndItsExtrasToTheFolderOfTheNewWorkingFolder()
    {
        Write("projects/-old/abc.jsonl", "{\"a\":1}");
        Write("projects/-old/abc/subagents/one.jsonl", "{}");
        string project = Directory.CreateDirectory(temp.Combine("my project.v2")).FullName;
        ClaudeEngine engine = new("claude", temp.Path);

        await engine.MoveSession("abc", project);

        string folder = temp.Combine($"projects/{Regex.Replace(project, "[^a-zA-Z0-9]", "-")}");
        Assert.Equal("{\"a\":1}", (await File.ReadAllTextAsync(Path.Combine(folder, "abc.jsonl"))).Trim());
        Assert.True(File.Exists(Path.Combine(folder, "abc/subagents/one.jsonl")));
        Assert.True(File.Exists(temp.Combine("projects/-old/abc.jsonl")));
    }

    [Fact]
    public async Task Claude_MoveSession_ToAFolderThatAlreadyHasTheConversation_LeavesItAlone()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        string folder = Regex.Replace(project, "[^a-zA-Z0-9]", "-");
        Write("projects/-old/abc.jsonl", "old");
        Write($"projects/{folder}/abc.jsonl", "newer");

        await new ClaudeEngine("claude", temp.Path).MoveSession("abc", project);

        Assert.Equal("newer", (await File.ReadAllTextAsync(temp.Combine($"projects/{folder}/abc.jsonl"))).Trim());
    }

    [Fact]
    public async Task Claude_MoveSession_OfAConversationThatIsGone_Fails()
        => await Assert.ThrowsAsync<PromptSessionException>(() => new ClaudeEngine("claude", temp.Path).MoveSession("abc", temp.Path));

    [Fact]
    public async Task Claude_DeleteSession_AfterAMove_RemovesEveryCopy()
    {
        Write("projects/-old/abc.jsonl", "{}");
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        ClaudeEngine engine = new("claude", temp.Path);
        await engine.MoveSession("abc", project);

        await engine.DeleteSession("abc");

        Assert.False(engine.HasSession("abc"));
        Assert.Empty(Directory.EnumerateFiles(temp.Combine("projects"), "abc.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Codex_DeleteSession_RemovesTheConversationAndTheDateFoldersItLeavesEmptyButNotTheRoot()
    {
        Write("sessions/2026/01/01/rollout-2026-01-01T00-00-00-abc.jsonl", "{}");
        Write("sessions/2026/01/02/rollout-2026-01-02T00-00-00-other.jsonl", "{}");
        Write("sessions/2027/05/05/rollout-2027-05-05T00-00-00-last.jsonl", "{}");
        CodexEngine engine = new("codex", temp.Path);

        await engine.DeleteSession("abc");
        await engine.DeleteSession("last");

        Assert.False(Directory.Exists(temp.Combine("sessions/2026/01/01")));
        Assert.True(File.Exists(temp.Combine("sessions/2026/01/02/rollout-2026-01-02T00-00-00-other.jsonl")));
        Assert.False(Directory.Exists(temp.Combine("sessions/2027")));
        Assert.True(Directory.Exists(temp.Combine("sessions")));
        await engine.DeleteSession("missing");
    }
}
