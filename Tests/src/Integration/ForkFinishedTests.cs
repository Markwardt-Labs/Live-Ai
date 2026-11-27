namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ForkFinishedTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly DateTimeOffset start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
        => temp.Dispose();

    private string Stamp(int minute)
        => start.AddMinutes(minute).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private ClaudeEngine Claude => new("claude", temp.Combine("claude-state"));

    private string Project(string folder)
        => Directory.CreateDirectory(temp.Combine($"claude-state/projects/{Regex.Replace(folder, "[^a-zA-Z0-9]", "-")}")).FullName;

    private PromptEngineStart Source(string? id = "origin", string? forked = null)
        => new() { WorkingFolder = temp.Combine("work"), AiSessionId = id, ForkedFrom = forked, Instructions = string.Empty };

    private string[] Transcript(string id) =>
    [
        $$"""{"type":"queue-operation","sessionId":"{{id}}","timestamp":"{{Stamp(0)}}"}""",
        $$$"""{"type":"user","uuid":"u1","sessionId":"{{{id}}}","timestamp":"{{{Stamp(0)}}}","message":{"role":"user","content":"first"}}""",
        $$$"""{"type":"assistant","uuid":"a1","sessionId":"{{{id}}}","timestamp":"{{{Stamp(1)}}}","message":{"role":"assistant","content":[{"type":"text","text":"one"}]}}""",
        $$"""{"type":"system","subtype":"compact_boundary","uuid":"c1","sessionId":"{{id}}","timestamp":"{{Stamp(2)}}"}""",
        $$"""{"type":"last-prompt","sessionId":"{{id}}"}""",
        $$$"""{"type":"user","uuid":"u2","sessionId":"{{{id}}}","timestamp":"{{{Stamp(10)}}}","message":{"role":"user","content":"second"}}""",
        $$$"""{"type":"assistant","uuid":"a2","sessionId":"{{{id}}}","timestamp":"{{{Stamp(11)}}}","message":{"role":"assistant","content":[{"type":"text","text":"two"}]}}"""
    ];

    [Fact]
    public async Task Claude_BranchFinished_CopiesWhatTheConversationHeldBeforeTheMomentAsAConversationOfItsOwn()
    {
        string folder = Project(temp.Combine("work"));
        string original = Path.Combine(folder, "origin.jsonl");
        await File.WriteAllLinesAsync(original, Transcript("origin"));
        Directory.CreateDirectory(Path.Combine(folder, "origin"));
        await File.WriteAllTextAsync(Path.Combine(folder, "origin", "result.txt"), "a long tool result");
        // A copy that an earlier move left behind, which is older and must not be what is copied.
        await File.WriteAllLinesAsync(Path.Combine(Project(temp.Combine("elsewhere")), "origin.jsonl"), Transcript("origin").Take(1));

        PromptBranch branch = await ((IPromptEngine)Claude).BranchFinished(Source(), start.AddMinutes(10));

        string id = Assert.IsType<string>(branch.AiSessionId);
        Assert.Null(branch.ForkedFrom);
        Assert.Equal(Transcript(id).Take(5), await File.ReadAllLinesAsync(Path.Combine(folder, $"{id}.jsonl")));
        Assert.Equal("a long tool result", await File.ReadAllTextAsync(Path.Combine(folder, id, "result.txt")));
        Assert.Equal(Transcript("origin"), await File.ReadAllLinesAsync(original));
        Assert.Empty(Directory.GetFiles(folder, "*.partial"));
    }

    [Theory]
    [InlineData("a", "z")]
    [InlineData("z", "a")]
    public async Task Claude_ReadHistory_ReadsTheCopyThatWasWrittenLastOfAMovedConversation(string left, string used)
    {
        string stale = Path.Combine(Project(temp.Combine(left)), "origin.jsonl");
        await File.WriteAllLinesAsync(stale, Transcript("origin").Take(3));
        File.SetLastWriteTimeUtc(stale, start.UtcDateTime);
        await File.WriteAllLinesAsync(Path.Combine(Project(temp.Combine(used)), "origin.jsonl"), Transcript("origin"));

        List<PromptHistoryEntry> history = await ((IPromptEngine)Claude).ReadHistory("origin").ToListAsync();

        Assert.Equal("two", history[0].Text);
    }

    [Fact]
    public async Task Claude_MoveSession_BackToAFolderItWasInBringsWhatWasSaidSince()
    {
        string old = Path.Combine(Project(temp.Combine("work")), "origin.jsonl");
        await File.WriteAllLinesAsync(old, Transcript("origin").Take(3));
        File.SetLastWriteTimeUtc(old, start.UtcDateTime);
        string other = Path.Combine(Project(temp.Combine("other")), "origin.jsonl");
        await File.WriteAllLinesAsync(other, Transcript("origin"));
        Directory.CreateDirectory(temp.Combine($"claude-state/projects/{Regex.Replace(temp.Combine("other"), "[^a-zA-Z0-9]", "-")}/origin"));
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(other)!, "origin", "result.txt"), "made since");

        await ((IPromptEngine)Claude).MoveSession("origin", temp.Combine("work"));

        Assert.Equal(Transcript("origin"), await File.ReadAllLinesAsync(old));
        Assert.Equal("made since", await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(old)!, "origin", "result.txt")));
        Assert.Equal(Transcript("origin"), await File.ReadAllLinesAsync(other));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(old)!, "*.partial"));
    }

    [Fact]
    public async Task Claude_BranchFinished_LeavesOutALineThatIsStillBeingWritten()
    {
        string folder = Project(temp.Combine("work"));
        await File.WriteAllTextAsync(Path.Combine(folder, "origin.jsonl"), string.Join('\n', Transcript("origin").Take(3)) + "\n{\"type\":\"assistant\",\"uuid\":\"a");

        PromptBranch branch = await ((IPromptEngine)Claude).BranchFinished(Source(), start.AddMinutes(60));

        Assert.Equal(Transcript(branch.AiSessionId!).Take(3), await File.ReadAllLinesAsync(Path.Combine(folder, $"{branch.AiSessionId}.jsonl")));
    }

    [Fact]
    public async Task Claude_BranchFinished_HasNoConversationWhenTheAiHadAnsweredNothingByThen()
    {
        string folder = Project(temp.Combine("work"));
        await File.WriteAllLinesAsync(Path.Combine(folder, "origin.jsonl"), Transcript("origin"));

        PromptBranch branch = await ((IPromptEngine)Claude).BranchFinished(Source(), start.AddSeconds(30));

        Assert.Equal((null, null), (branch.AiSessionId, branch.ForkedFrom));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Claude_BranchFinished_OfABranchThatNeverHadAPromptStaysThatBranch()
    {
        PromptBranch branch = await ((IPromptEngine)Claude).BranchFinished(Source(null, "origin"), start);

        Assert.Equal("origin", branch.ForkedFrom);
    }

    [Fact]
    public async Task Codex_BranchFinished_ForksAfterTheLastThingTheAiDidBeforeTheMoment()
    {
        IPromptEngine codex = new CodexEngine(new FakeCodex(temp).Script(), temp.Combine("codex-state"));
        Directory.CreateDirectory(temp.Combine("codex-state/sessions/2026/01/01"));
        await File.WriteAllLinesAsync(temp.Combine("codex-state/sessions/2026/01/01/rollout-2026-01-01T00-00-00-codex-thread.jsonl"),
        [
            """{"type":"session_meta","payload":{"id":"codex-thread"}}""",
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1"}}""",
            $$$"""{"timestamp":"{{{Stamp(0)}}}","type":"event_msg","payload":{"type":"user_message","message":"first"}}""",
            $$$"""{"timestamp":"{{{Stamp(1)}}}","type":"event_msg","payload":{"type":"agent_message","message":"one"}}""",
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"turn-2"}}""",
            $$$"""{"timestamp":"{{{Stamp(10)}}}","type":"event_msg","payload":{"type":"user_message","message":"second"}}""",
            $$$"""{"timestamp":"{{{Stamp(11)}}}","type":"event_msg","payload":{"type":"agent_message","message":"two"}}"""
        ]);

        PromptBranch branch = await codex.BranchFinished(Source("codex-thread"), start.AddMinutes(10));

        Assert.Equal("codex-fork-1", branch.AiSessionId);
        Assert.Equal("turn-1", Assert.Single(await new FakeCodex(temp).Parameters("thread/fork")).GetProperty("lastTurnId").GetString());

        PromptBranch none = await codex.BranchFinished(Source("codex-thread"), start.AddSeconds(30));

        Assert.Null(none.AiSessionId);
    }
}
