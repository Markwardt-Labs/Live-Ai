namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ForkSessionTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptEngine? claude;

    private IPromptEngine? codex;

    private IPromptEngine Claude => claude ??= new ClaudeEngine(temp.Script("claude", "mkdir -p \"$CLAUDE_CONFIG_DIR/projects/p\"\nn=$(ls \"$PWD\" | grep -c '^run-' || true)\nprintf '%s\\n' \"$@\" > \"$PWD/run-$n.txt\"\nIFS= read -r line\nid=claude-$n\n: > \"$CLAUDE_CONFIG_DIR/projects/p/$id.jsonl\"\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}'\necho \"{\\\"type\\\":\\\"result\\\",\\\"is_error\\\":false,\\\"session_id\\\":\\\"$id\\\"}\""), temp.Combine("claude-state"));

    private IPromptEngine Codex => codex ??= new CodexEngine(new FakeCodex(temp).Script(), temp.Combine("codex-state"));

    private IPromptProvider Provider(IPromptEngine engine)
        => new PromptProvider(engine, temp.Combine("data"));

    private async Task<IPromptSession> Create(IPromptEngine provider)
        => temp.Track(await Provider(provider).Create(TestModels.Options));

    private async Task<JsonElement> Stored(IPromptSession session)
        => JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine($"data/Sessions/{session.Id}.json"))).RootElement;

    private async Task<string[]> Run(int number)
        => await File.ReadAllLinesAsync(temp.Combine($"run-{number}.txt"));

    [Fact]
    public async Task Fork_OfAWorkspaceSession_RunsInTheSameFolderWithTheSameWorkspace()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        await using IPromptSession origin = temp.Track(await Provider(Claude).Create(new PromptSessionOptions { Model = TestModels.Model, WorkspacePath = project, Sandbox = false }));
        await origin.Request("first");

        await using IPromptSession fork = temp.Track(await origin.Fork());

        JsonElement stored = await Stored(fork);
        Assert.Equal(project, stored.GetProperty("workingFolder").GetString());
        Assert.Equal(project, stored.GetProperty("workspacePath").GetString());
        Assert.False(stored.GetProperty("sandbox").GetBoolean());
        Assert.Equal(project, Assert.IsAssignableFrom<IDiskStore>(fork.Configuration.Workspace).Path);
    }

    [Fact]
    public async Task Claude_Fork_StartsAtTheFirstPromptByResumingTheOriginalAsACopy()
    {
        await using IPromptSession origin = await Create(Claude);
        await origin.Request("first");

        await using IPromptSession fork = temp.Track(await origin.Fork());

        Assert.NotEqual(origin.Id, fork.Id);
        JsonElement stored = await Stored(fork);
        Assert.Equal("claude-0", stored.GetProperty("forkedFrom").GetString());
        Assert.False(stored.TryGetProperty("aiSessionId", out JsonElement id) && id.ValueKind == JsonValueKind.String);

        await fork.Request("second");

        string[] arguments = await Run(1);
        Assert.Equal(["--resume", "claude-0", "--fork-session", "--system-prompt-snapshot", "off"], arguments.TakeLast(5));
        stored = await Stored(fork);
        Assert.Equal("claude-1", stored.GetProperty("aiSessionId").GetString());
        Assert.True(!stored.TryGetProperty("forkedFrom", out JsonElement from) || from.ValueKind == JsonValueKind.Null);

        await fork.Request("third");

        Assert.Equal(["--resume", "claude-1", "--system-prompt-snapshot", "off"], (await Run(2)).TakeLast(4));
        await origin.Request("again");
        Assert.Equal(["--resume", "claude-0", "--system-prompt-snapshot", "off"], (await Run(3)).TakeLast(4));
    }

    [Fact]
    public async Task Codex_Fork_ForksTheThreadAtOnceAndResumesTheCopy()
    {
        await using IPromptSession origin = await Create(Codex);
        await origin.Request("first");

        await using IPromptSession fork = temp.Track(await origin.Fork());

        Assert.Equal("codex-fork-1", (await Stored(fork)).GetProperty("aiSessionId").GetString());
        JsonElement forked = Assert.Single(await new FakeCodex(temp).Parameters("thread/fork"));
        Assert.Equal("codex-thread", forked.GetProperty("threadId").GetString());

        await fork.Request("second");

        Assert.Equal("codex-fork-1", Assert.Single(await new FakeCodex(temp).Parameters("thread/resume"), request => request.GetProperty("threadId").GetString() == "codex-fork-1").GetProperty("threadId").GetString());
    }

    [Fact]
    public async Task Fork_AtAnEntryOfTheHistory_BranchesThroughTheTurnOfThatEntry()
    {
        await using IPromptSession origin = await Create(Codex);
        await origin.Request("first");
        await origin.Request("second");
        string rollout = Directory.GetFiles(temp.Combine("codex-state/sessions"), "*codex-thread.jsonl", SearchOption.AllDirectories).Single();
        await File.WriteAllLinesAsync(rollout,
        [
            """{"type":"session_meta","payload":{"id":"codex-thread"}}""",
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1"}}""",
            """{"type":"event_msg","payload":{"type":"user_message","message":"first"}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"one"}}""",
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"turn-2"}}""",
            """{"type":"event_msg","payload":{"type":"user_message","message":"second"}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"two"}}"""
        ]);
        PromptHistoryEntry first = (await origin.GetHistory().Take(10).ToListAsync()).Single(entry => entry.Text == "first");

        await using IPromptSession fork = temp.Track(await origin.Fork(first));

        Assert.Equal("turn-1", Assert.Single(await new FakeCodex(temp).Parameters("thread/fork")).GetProperty("lastTurnId").GetString());
        Assert.Equal("codex-fork-1", (await Stored(fork)).GetProperty("aiSessionId").GetString());
    }

    [Fact]
    public async Task AttachTo_MakesTheNextPromptResumeTheGivenClaudeSessionAndStoresIt()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        IPromptProvider provider = Provider(Claude);
        await using IPromptSession first = temp.Track(await provider.Create(new PromptSessionOptions { Model = TestModels.Model, WorkspacePath = project }));
        await first.Request("one");
        await using IPromptSession second = temp.Track(await provider.Create(new PromptSessionOptions { Model = TestModels.Model, WorkspacePath = project }));

        await second.AttachTo("claude-0");
        await second.Request("two");

        string[] arguments = await Run(1);
        Assert.Equal("claude-0", arguments[Array.IndexOf(arguments, "--resume") + 1]);
        Assert.Equal("claude-1", (await Stored(second)).GetProperty("aiSessionId").GetString());
    }

    [Fact]
    public async Task AttachTo_AnotherWorkspace_RunsTheNextPromptThereAndLoadsWithIt()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        IPromptProvider provider = Provider(Claude);
        IPromptSession session = await provider.Create(TestModels.Options);
        await session.Request("one");

        await session.AttachTo("claude-0", new PromptSessionOptions { Model = TestModels.Model, WorkspacePath = project, Sandbox = false });
        await session.Request("two");
        string id = session.Id;
        await session.DisposeAsync();
        await using IPromptSession loaded = temp.Track(await provider.Load(id));

        string[] arguments = await Run(1);
        Assert.Equal("claude-0", arguments[Array.IndexOf(arguments, "--resume") + 1]);
        Assert.Equal(project, Assert.IsAssignableFrom<IDiskStore>(loaded.Configuration.Workspace).Path);
        Assert.Equal(project, JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine($"data/Sessions/{id}.json"))).RootElement.GetProperty("workingFolder").GetString());
    }

    [Fact]
    public async Task AttachTo_ASessionThatDoesNotExist_FailsAndLeavesTheLink()
    {
        await using IPromptSession session = await Create(Claude);
        await session.Request("one");

        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => session.AttachTo("no-such-session"));

        Assert.Equal("claude-0", (await Stored(session)).GetProperty("aiSessionId").GetString());
    }

    [Fact]
    public async Task Rewind_ContinuesTheSameSessionInABranchAtTheEntry()
    {
        await using IPromptSession session = await Create(Codex);
        await session.Request("first");
        string rollout = Directory.GetFiles(temp.Combine("codex-state/sessions"), "*codex-thread.jsonl", SearchOption.AllDirectories).Single();
        await File.WriteAllLinesAsync(rollout,
        [
            """{"type":"session_meta","payload":{"id":"codex-thread"}}""",
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1"}}""",
            """{"type":"event_msg","payload":{"type":"user_message","message":"first"}}"""
        ]);
        PromptHistoryEntry first = (await session.GetHistory().Take(10).ToListAsync()).Single();

        await session.Rewind(first);
        await session.Request("again");

        Assert.Equal("codex-fork-1", (await Stored(session)).GetProperty("aiSessionId").GetString());
        Assert.Contains(await new FakeCodex(temp).Parameters("thread/resume"), request => request.GetProperty("threadId").GetString() == "codex-fork-1");
    }

    [Fact]
    public async Task Rewind_OfASessionWithoutAConversation_Fails()
    {
        await using IPromptSession session = await Create(Codex);

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Rewind(new PromptHistoryEntry { Event = new PromptInputEvent { Text = "x" } }));
        await Assert.ThrowsAsync<PromptSessionException>(() => session.Fork(new PromptHistoryEntry { Event = new PromptInputEvent { Text = "x" } }));
    }

    [Fact]
    public async Task Fork_CarriesTheConfigurationAndTheToolsButHasAScratchFolderOfItsOwn()
    {
        await using IPromptSession origin = await Create(Claude);
        IStore notes = IStore.FromMemory();
        PromptTool tool = PromptTool.Create<Question, string>("ask", "asks", (_, _) => Task.FromResult("a"));
        Func<PromptToolCallEvent, CancellationToken, Task<PromptToolDecision>> approval = (_, _) => Task.FromResult(PromptToolDecision.Allow);
        origin.Configuration.ModelSettings["effort"] = "high";
        origin.Configuration.ModelSettings["fast"] = "true";
        origin.Configuration.EnableShell = true;
        origin.Configuration.EnableImages = true;
        origin.Configuration.EnableWeb = true;
        origin.Configuration.EnableSearch = true;
        Func<Uri, CancellationToken, Task<bool>> filter = (_, _) => Task.FromResult(false);
        origin.Configuration.WebFilter = filter;
        origin.Configuration.BeforeToolCall = approval;
        origin.Configuration.Workspace = notes;
        origin.Configuration.Instructions = "Be brief.";
        origin.Configuration.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");
        origin.Configuration.Tools.Add(tool);
        origin.Configuration.Mounts["notes"] = notes;

        await using IPromptSession fork = temp.Track(await origin.Fork());

        Assert.Equal(origin.Configuration.Model, fork.Configuration.Model);
        Assert.Equal(origin.Configuration.ModelSettings.OrderBy(entry => entry.Key), fork.Configuration.ModelSettings.OrderBy(entry => entry.Key));
        Assert.Equal("high", fork.Configuration.ModelSettings["effort"]);
        Assert.True(fork.Configuration.EnableShell);
        Assert.True(fork.Configuration.EnableImages);
        Assert.True(fork.Configuration.EnableWeb);
        Assert.True(fork.Configuration.EnableSearch);
        Assert.Same(filter, fork.Configuration.WebFilter);
        Assert.Same(approval, fork.Configuration.BeforeToolCall);
        Assert.Same(notes, fork.Configuration.Workspace);
        Assert.Equal("Be brief.", fork.Configuration.Instructions);
        Assert.Equal(origin.Configuration.McpServers["db"], fork.Configuration.McpServers["db"]);
        Assert.Equal([tool], fork.Configuration.Tools);
        Assert.Same(notes, fork.Configuration.Mounts["notes"]);
        Assert.Equal(origin.Provider, fork.Provider);
        Assert.True(Directory.Exists(Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{fork.Id}")));
    }

    [Fact]
    public async Task Fork_OfASessionWithoutPrompts_IsAFreshSessionWithTheSameConfiguration()
    {
        await using IPromptSession origin = await Create(Claude);
        origin.Configuration.Instructions = "Be brief.";

        await using IPromptSession fork = temp.Track(await origin.Fork());
        await fork.Request("hello");

        Assert.DoesNotContain("--resume", await Run(0));
        Assert.Equal("Be brief.", fork.Configuration.Instructions);
    }

    [Fact]
    public async Task Fork_OfABranchThatWasNotPromptedYet_BranchesFromTheSameOriginal()
    {
        await using IPromptSession origin = await Create(Claude);
        await origin.Request("first");
        await using IPromptSession fork = temp.Track(await origin.Fork());

        await using IPromptSession again = temp.Track(await fork.Fork());

        Assert.Equal("claude-0", (await Stored(again)).GetProperty("forkedFrom").GetString());
    }

    [Fact]
    public async Task Fork_OfADisposedSession_Fails()
    {
        IPromptSession origin = await Create(Claude);
        await origin.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => origin.Fork());
    }

    [Fact]
    public async Task ALoadedBranch_ThatWasNotPromptedYet_NeedsTheOriginalConversationToStillExist()
    {
        IPromptProvider provider = Provider(Claude);
        await using IPromptSession origin = temp.Track(await provider.Create(TestModels.Options));
        await origin.Request("first");
        string forkId;

        await using (IPromptSession fork = temp.Track(await origin.Fork()))
        {
            forkId = fork.Id;
        }

        await using IPromptSession loaded = temp.Track(await provider.Load(forkId));
        Assert.NotNull(loaded);
        File.Delete(temp.Combine("claude-state/projects/p/claude-0.jsonl"));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => provider.Load(forkId));
    }

    [Fact]
    public async Task History_ReadsTheStoredConversationOfTheSessionOrTheOneItBranchedFrom()
    {
        await using IPromptSession origin = await Create(Claude);
        Assert.Empty(await origin.GetHistory().Take(100).ToListAsync());
        await origin.Request("first");
        await File.WriteAllTextAsync(temp.Combine("claude-state/projects/p/claude-0.jsonl"), """{"type":"user","message":{"role":"user","content":"hello"}}""" + "\n");

        await using IPromptSession fork = temp.Track(await origin.Fork());

        Assert.Equal(["hello"], (await origin.GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text));
        Assert.Equal(["hello"], (await fork.GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text));
        await origin.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => origin.GetHistory());
    }
}
