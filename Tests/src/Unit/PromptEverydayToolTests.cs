namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptEverydayToolTests
{
    private readonly ManualTime time = new();

    private async Task<string> Run(PromptTool tool, string json)
        => await tool.Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);

    private PromptTool Tool(string name, string description)
        => new() { Name = name, Description = description, InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"x":{"type":"string"}}}""").RootElement, Handler = (_, _) => Task.FromResult("ok") };

    [Fact]
    public async Task CurrentTime_TellsUtcAndTheLocalZoneOfTheClockItIsGiven()
    {
        PromptTool tool = new PromptLibraryTools(null, time).CreateTime();

        string text = await Run(tool, "{}");

        Assert.Equal("current_time", tool.Name);
        Assert.Equal("current_time", new PromptLibraryTools().TimeName);
        Assert.Contains("UTC: Thursday 2026-01-01 00:00:00", text, StringComparison.Ordinal);
        Assert.Contains("Local: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentTime_InAZoneTheAiNames_TellsTheTimeThere()
    {
        PromptTool tool = new PromptLibraryTools(null, time).CreateTime();

        Assert.Contains("Thursday 2026-01-01 09:00:00 +09:00 (Asia/Tokyo)", await Run(tool, """{"time_zone":"Asia/Tokyo"}"""), StringComparison.Ordinal);
        Assert.StartsWith("Thursday 2026-01-01 00:00:00 +00:00 (UTC)", await Run(tool, """{"time_zone":"UTC"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentTime_RefusesAZoneItDoesNotKnow()
    {
        PromptTool tool = new PromptLibraryTools(null, time).CreateTime();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(tool, """{"time_zone":"Mars/Olympus"}"""));

        Assert.Contains("Mars/Olympus", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolSearch_RanksByTheNameBeforeTheDescriptionAndGivesTheInputSchema()
    {
        PromptTool[] tools = [Tool("read_file", "Reads a text file"), Tool("send_email", "Sends a message to someone; a file can be attached"), Tool("weather", "Looks up the forecast")];
        PromptTool search = new PromptLibraryTools().CreateToolSearch(() => tools);

        string text = await Run(search, """{"query":"file"}""");

        Assert.Equal("tool_search", search.Name);
        Assert.Equal("tool_search", new PromptLibraryTools().ToolSearchName);
        Assert.StartsWith("read_file: Reads a text file\ninput schema: {", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("send_email", StringComparison.Ordinal) > text.IndexOf("read_file", StringComparison.Ordinal));
        Assert.DoesNotContain("weather", text, StringComparison.Ordinal);
        Assert.Contains("\"properties\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolSearch_FindsAToolByItsExactNameAndLimitsTheCount()
    {
        PromptTool[] tools = [.. Enumerable.Range(1, 8).Select(number => Tool($"tool_{number}", "common words")), Tool("exact", "unrelated")];
        PromptTool search = new PromptLibraryTools().CreateToolSearch(() => tools);

        string exact = await Run(search, """{"query":"exact"}""");
        string limited = await Run(search, """{"query":"common","max_results":3}""");

        Assert.StartsWith("exact: unrelated", exact, StringComparison.Ordinal);
        Assert.Equal(3, limited.Split("input schema:").Length - 1);
    }

    [Fact]
    public async Task ToolSearch_WithoutWordsListsTheNamesAndWithWordsThatMatchNothingSaysSo()
    {
        PromptTool[] tools = [Tool("a_tool", "x"), Tool("b_tool", "y")];
        PromptTool search = new PromptLibraryTools().CreateToolSearch(() => tools);

        Assert.Equal("2 tools:\n- a_tool\n- b_tool", await Run(search, "{}"));
        Assert.StartsWith("No tool matches 'zzz'", await Run(search, """{"query":"zzz"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolSearch_SeesTheToolsAsTheyAreWhenItIsCalled()
    {
        List<PromptTool> tools = [Tool("first", "a")];
        PromptTool search = new PromptLibraryTools().CreateToolSearch(() => tools);

        tools.Add(Tool("second", "b"));

        Assert.Contains("second", await Run(search, "{}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegate_GivesTheTaskAndTheChoiceOfSharingTheConversationToTheSessionAndReturnsItsAnswer()
    {
        List<(string Task, bool Share)> asked = [];
        PromptTool tool = new PromptLibraryTools().CreateDelegate((task, share, _) =>
        {
            asked.Add((task, share));
            return Task.FromResult("found it");
        }, new PromptTasks());

        string plain = await Run(tool, """{"task":"  look into x  "}""");
        string shared = await Run(tool, """{"task":"look into y","share_context":true}""");

        Assert.Equal(("delegate", "found it", "found it"), (tool.Name, plain, shared));
        Assert.Equal("delegate", new PromptLibraryTools().DelegateName);
        Assert.Equal([("look into x", false), ("look into y", true)], asked);
    }

    [Fact]
    public async Task Delegate_RefusesAnEmptyTask()
    {
        PromptTool tool = new PromptLibraryTools().CreateDelegate((_, _, _) => Task.FromResult("never"), new PromptTasks());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(tool, """{"task":" "}"""));
    }

    [Fact]
    public async Task DiffFiles_ShowsWhatChangedAsAUnifiedDiffWithContext()
    {
        IStore store = await TestStoreExtensions.Memory(new Dictionary<string, string>
        {
            ["a.txt"] = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\n",
            ["b.txt"] = "one\ntwo\nTHREE\nfour\nfive\nsix\nseven\neight\nnine\nten\nextra\n"
        });
        PromptTool tool = new PromptLibraryTools().Create(store).Single(created => created.Name == "diff_files");

        string diff = await Run(tool, """{"path_a":"a.txt","path_b":"b.txt","context_lines":1}""");

        Assert.Equal("--- a.txt\n+++ b.txt\n@@ -2,3 +2,3 @@\n two\n-three\n+THREE\n four\n@@ -10,1 +10,2 @@\n ten\n+extra\n", diff);
    }

    [Fact]
    public async Task DiffFiles_SaysWhenTheFilesAreTheSameAndIgnoresLineEndings()
    {
        IStore store = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["a.txt"] = "x\r\ny\r\n", ["b.txt"] = "x\ny\n", ["c.txt"] = "x\ny" });
        PromptTool tool = new PromptLibraryTools().Create(store).Single(created => created.Name == "diff_files");

        Assert.Equal("The files are identical.", await Run(tool, """{"path_a":"a.txt","path_b":"b.txt"}"""));
        Assert.Equal("The files are identical.", await Run(tool, """{"path_a":"b.txt","path_b":"c.txt"}"""));
    }

    [Fact]
    public async Task DiffFiles_RefusesMissingPathsBinaryFilesAndFilesThatAreNotThere()
    {
        IStore store = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["a.txt"] = "x", ["bin.dat"] = "a\0b" });
        PromptTool tool = new PromptLibraryTools().Create(store).Single(created => created.Name == "diff_files");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(tool, """{"path_a":"a.txt"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(tool, """{"path_a":"a.txt","path_b":"bin.dat"}"""));
        await Assert.ThrowsAnyAsync<IOException>(() => Run(tool, """{"path_a":"a.txt","path_b":"missing.txt"}"""));
    }

    [Fact]
    public async Task DiffFiles_CutsAVeryLongDiffAndRefusesFilesThatDifferInTooManyLines()
    {
        IStore store = await TestStoreExtensions.Memory(new Dictionary<string, string>
        {
            ["a.txt"] = string.Join('\n', Enumerable.Range(0, 3000).Select(number => $"old line {number}")),
            ["b.txt"] = string.Join('\n', Enumerable.Range(0, 3000).Select(number => $"new line {number}")),
            ["c.txt"] = string.Join('\n', Enumerable.Range(0, 900).Select(number => $"old line {number} with a long tail to make the diff long enough to be cut {new string('x', 30)}")),
            ["d.txt"] = string.Join('\n', Enumerable.Range(0, 900).Select(number => $"new line {number} with a long tail to make the diff long enough to be cut {new string('y', 30)}"))
        });
        PromptTool tool = new PromptLibraryTools().Create(store).Single(created => created.Name == "diff_files");

        InvalidOperationException tooMany = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(tool, """{"path_a":"a.txt","path_b":"b.txt"}"""));
        string cut = await Run(tool, """{"path_a":"c.txt","path_b":"d.txt"}""");

        Assert.Contains("too many", tooMany.Message, StringComparison.Ordinal);
        Assert.EndsWith("[cut after 40000 characters]", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileToolsIncludeTheDiffAndTheReservedNamesDo()
    {
        PromptLibraryTools tools = new();

        Assert.Contains("diff_files", tools.Names);
        Assert.Contains("diff_files", tools.Create(new MemoryStore()).Select(tool => tool.Name));
    }
}
