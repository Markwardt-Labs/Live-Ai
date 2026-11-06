namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class PromptToolSessionTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly List<string> calls = [];

    public void Dispose()
        => temp.Dispose();

    private string CallTool(string tool, string arguments)
        => $$$"""
        config=''
        prev=''
        for argument in "$@"; do
        if [ "$prev" = '--mcp-config' ]; then config="$argument"; fi
        prev="$argument"
        done
        url=$(printf '%s' "$config" | sed 's/.*"url":"\([^"]*\)".*/\1/')
        curl -s -H "Authorization: Bearer $LIVEAI_TOOLS_TOKEN" -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}' "$url" > "$PWD/tool-result.txt"
        """;

    private IPromptProvider Provider(IPromptEngine engine)
        => TestProviders.Make(engine, temp.Combine("data"));

    private IPromptEngine Claude(string claude)
        => new ClaudeEngine(temp.Script("claude", $"IFS= read -r line\nprintf '%s\\n' \"$@\" > \"$PWD/args.txt\"\n{claude}\necho '{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"done\"}}]}}}}'\necho '{{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\"}}'"), temp.Combine("claude-state"));

    private PromptTool Echo()
        => PromptTool.Create<Details, string>("echo", "Echoes the note.", (input, _) =>
        {
            lock (calls)
            {
                calls.Add(input.Note);
            }

            return Task.FromResult($"echo: {input.Note}");
        });

    [Fact]
    public async Task Ask_LetsTheToolReachTheCallersFunctionWithTheSecretItWasGiven()
    {
        await using IPromptSession session = temp.Track(await Provider(Claude(CallTool("echo", """{"note":"from the tool"}"""))).Create(TestModels.Options));
        session.Transient.Tools.Add(Echo());

        Assert.Equal("done", await session.Request("use it"));

        Assert.Equal(["from the tool"], calls);
        Assert.Contains("echo: from the tool", await File.ReadAllTextAsync(temp.Combine("tool-result.txt")));
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Contains("mcp__liveai__echo", arguments[Array.IndexOf(arguments, "--allowedTools") + 1].Split(','));
        Assert.DoesNotContain(arguments, argument => argument.Contains(Environment.GetEnvironmentVariable("LIVEAI_TOOLS_TOKEN") ?? "none-set"));
    }

    [Fact]
    public async Task Ask_AfterTheSessionIsDisposed_TheToolIsNoLongerReachable()
    {
        IPromptSession session = temp.Track(await Provider(Claude("")).Create(TestModels.Options));
        session.Transient.Tools.Add(Echo());
        await session.Request("use it");
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        string url = JsonDocument.Parse(arguments[Array.IndexOf(arguments, "--mcp-config") + 1]).RootElement.GetProperty("mcpServers").GetProperty("liveai").GetProperty("url").GetString()!;

        await session.DisposeAsync();

        using HttpClient client = new();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.PostAsync(url, new StringContent("{}")));
    }

    private string Call(string tool, string arguments, string output)
        => $$$"""
        curl -s -H "Authorization: Bearer $LIVEAI_TOOLS_TOKEN" -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}' "$url" > "$PWD/{{{output}}}"
        """;

    private string Library(params string[] calls)
        => string.Join("\n", ["config=''", "prev=''", "for argument in \"$@\"; do", "if [ \"$prev\" = '--mcp-config' ]; then config=\"$argument\"; fi", "prev=\"$argument\"", "done", "url=$(printf '%s' \"$config\" | sed 's/.*\"url\":\"\\([^\"]*\\)\".*/\\1/')", .. calls]);

    private async Task<IPromptSession> Session(string script, string? workspace = null)
    {
        if (workspace is not null)
        {
            Directory.CreateDirectory(workspace);
        }

        return temp.Track(await Provider(Claude(script)).Create(workspace is null ? TestModels.Options : TestModels.With(workspacePath: workspace)));
    }

    [Fact]
    public async Task Ask_GivesTheAiOnlyTheLibraryFileToolsAndNoNativeOnes()
    {
        await using IPromptSession session = await Session(string.Empty);

        await session.Request("work");

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Equal(string.Empty, arguments[Array.IndexOf(arguments, "--tools") + 1]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.Equal("mcp__liveai__list_directory,mcp__liveai__find_files,mcp__liveai__search_files,mcp__liveai__read_file,mcp__liveai__write_file,mcp__liveai__edit_file,mcp__liveai__delete_file,mcp__liveai__move_file,mcp__liveai__copy_file,mcp__liveai__diff_files,mcp__liveai__view_image,mcp__liveai__current_time,mcp__liveai__tool_search,mcp__liveai__delegate,mcp__liveai__respond", arguments[Array.IndexOf(arguments, "--allowedTools") + 1]);
        string guidance = arguments[Array.IndexOf(arguments, "--append-system-prompt") + 1];
        Assert.Contains("file tools of the MCP server named liveai", guidance);
        Assert.Contains("unless it starts with CACHE:", guidance);
    }

    [Fact]
    public async Task Ask_WithTheCallersTools_OffersThemBesideTheFileTools()
    {
        await using IPromptSession session = await Session(string.Empty);
        session.Transient.Tools.Add(Echo());

        await session.Request("work");

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Equal("mcp__liveai__list_directory,mcp__liveai__find_files,mcp__liveai__search_files,mcp__liveai__read_file,mcp__liveai__write_file,mcp__liveai__edit_file,mcp__liveai__delete_file,mcp__liveai__move_file,mcp__liveai__copy_file,mcp__liveai__diff_files,mcp__liveai__view_image,mcp__liveai__current_time,mcp__liveai__tool_search,mcp__liveai__delegate,mcp__liveai__echo,mcp__liveai__respond", arguments[Array.IndexOf(arguments, "--allowedTools") + 1]);
    }

    [Fact]
    public async Task Ask_LetsTheAiWriteAndReadFilesOfTheWorkspaceThroughTheLibraryToolsAndKeepsItInsideIt()
    {
        string script = Library(
            Call("write_file", """{"path":"notes/hello.txt","content":"hi there"}""", "write.txt"),
            Call("read_file", """{"path":"notes/hello.txt"}""", "read.txt"),
            Call("edit_file", """{"path":"notes/hello.txt","old_string":"hi","new_string":"hello"}""", "edit.txt"),
            Call("read_file", """{"path":"/etc/hostname"}""", "outside.txt"),
            Call("write_file", """{"path":"../loose.txt","content":"x"}""", "loose-result.txt"),
            Call("list_directory", "{}", "list.txt"));
        await using IPromptSession session = await Session(script, temp.Combine("work"));

        await session.Request("work");

        Assert.Equal("hello there", await File.ReadAllTextAsync(temp.Combine("work/notes/hello.txt")));
        Assert.Contains("1\\thi there", await File.ReadAllTextAsync(temp.Combine("read.txt")));
        Assert.Contains("Edited notes/hello.txt", await File.ReadAllTextAsync(temp.Combine("edit.txt")));
        Assert.Contains("\"isError\":true", await File.ReadAllTextAsync(temp.Combine("outside.txt")));
        Assert.Contains("\"isError\":true", await File.ReadAllTextAsync(temp.Combine("loose-result.txt")));
        Assert.False(File.Exists(temp.Combine("loose.txt")));
        string listing = await File.ReadAllTextAsync(temp.Combine("list.txt"));
        Assert.Contains("notes/", listing);
        Assert.Contains("CACHE:/", listing);
    }

    [Fact]
    public async Task Ask_LetsTheAiWorkInTheCacheAndTheCallerReadsTheResultAndNothingReachesTheDisk()
    {
        string script = Library(Call("read_file", """{"path":"CACHE:/a.txt"}""", "read.txt"), Call("write_file", """{"path":"CACHE:/out/b.txt","content":"made by the AI"}""", "write.txt"));
        await using IPromptSession session = await Session(script);
        await session.Cache.WriteText("a.txt", "from the caller");

        await session.Request("work");

        Assert.Contains("from the caller", await File.ReadAllTextAsync(temp.Combine("read.txt")));
        Assert.Equal("made by the AI", await session.Cache.ReadText("out/b.txt"));
        Assert.False(File.Exists(temp.Combine("out/b.txt")));
    }

    [Fact]
    public async Task Ask_WithAWorkspace_LetsTheAiCopyBetweenItAndTheCache()
    {
        Directory.CreateDirectory(temp.Combine("work"));
        await File.WriteAllTextAsync(temp.Combine("work/guide.md"), "the guide");
        string script = Library(
            Call("copy_file", """{"from":"guide.md","to":"CACHE:/guide-copy.md"}""", "copy.txt"),
            Call("move_file", """{"from":"CACHE:/guide-copy.md","to":"moved.md"}""", "move.txt"));
        await using IPromptSession session = await Session(script, temp.Combine("work"));

        await session.Request("work");

        Assert.Equal("the guide", await File.ReadAllTextAsync(temp.Combine("work/moved.md")));
        Assert.Empty(await session.Cache.List(string.Empty).ToListAsync());
    }

    [Fact]
    public async Task Ask_TheAiCannotDeleteOrRenameTheCacheFolderItself()
    {
        string script = Library(
            Call("delete_file", """{"path":"CACHE:"}""", "delete1.txt"),
            Call("delete_file", """{"path":"CACHE:/."}""", "delete2.txt"),
            Call("delete_file", """{"path":"CACHE:/"}""", "delete3.txt"),
            Call("move_file", """{"from":"CACHE:","to":"renamed"}""", "move1.txt"),
            Call("move_file", """{"from":"CACHE:/.","to":"cache2"}""", "move2.txt"),
            Call("write_file", """{"path":"CACHE:/.","content":"x"}""", "write.txt"),
            Call("list_directory", "{}", "list.txt"));
        await using IPromptSession session = await Session(script);
        await session.Cache.WriteText("keep.txt", "kept");

        await session.Request("work");

        foreach (string result in new[] { "delete1", "delete2", "delete3", "move1", "move2", "write" })
        {
            Assert.Contains("\"isError\":true", await File.ReadAllTextAsync(temp.Combine($"{result}.txt")));
        }

        Assert.Equal("kept", await session.Cache.ReadText("keep.txt"));
        Assert.Contains("CACHE:/", await File.ReadAllTextAsync(temp.Combine("list.txt")));
    }
}
