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
        => new PromptProvider(engine, temp.Combine("data"));

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
        await using IPromptSession session = temp.Track(await Provider(Claude(CallTool("echo", """{"note":"from the tool"}"""))).Create());
        session.Configuration.Tools.Add(Echo());

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
        IPromptSession session = temp.Track(await Provider(Claude("")).Create());
        session.Configuration.Tools.Add(Echo());
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

    private IStore Folder(string name)
    {
        Directory.CreateDirectory(temp.Combine(name));
        return IStore.FromDisk(temp.Combine(name));
    }

    private async Task<IPromptSession> Session(string script, Dictionary<string, IStore>? mounts = null)
    {
        IPromptSession session = temp.Track(await Provider(Claude(script)).Create());

        if (mounts is not null)
        {
            foreach ((string name, IStore store) in mounts)
            {
                session.Configuration.Mounts[name] = store;
            }
        }

        return session;
    }

    [Fact]
    public async Task Ask_WithMounts_GivesTheAiOnlyTheLibraryFileToolsAndNoNativeOnes()
    {
        await using IPromptSession session = await Session(string.Empty, new Dictionary<string, IStore> { ["work"] = Folder("work") });

        await session.Request("work");

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Equal(string.Empty, arguments[Array.IndexOf(arguments, "--tools") + 1]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.Equal("mcp__liveai__list_directory,mcp__liveai__find_files,mcp__liveai__search_files,mcp__liveai__read_file,mcp__liveai__write_file,mcp__liveai__edit_file,mcp__liveai__delete_file,mcp__liveai__move_file,mcp__liveai__copy_file,mcp__liveai__respond", arguments[Array.IndexOf(arguments, "--allowedTools") + 1]);
        Assert.Contains("file tools of the MCP server named liveai", arguments[Array.IndexOf(arguments, "--append-system-prompt") + 1]);
    }

    [Fact]
    public async Task Ask_WithoutMounts_GivesTheAiNoAccessToFilesAndSaysSo()
    {
        await using IPromptSession session = await Session(string.Empty);

        await session.Request("work");

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Equal(string.Empty, arguments[Array.IndexOf(arguments, "--tools") + 1]);
        Assert.Equal("mcp__liveai__respond", arguments[Array.IndexOf(arguments, "--allowedTools") + 1]);
        Assert.Contains("no access to files", arguments[Array.IndexOf(arguments, "--append-system-prompt") + 1]);
    }

    [Fact]
    public async Task Ask_WithOnlyTheCallersTools_SaysTheAiHasNoFiles()
    {
        await using IPromptSession session = await Session(string.Empty);
        session.Configuration.Tools.Add(Echo());

        await session.Request("work");

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Equal("mcp__liveai__echo,mcp__liveai__respond", arguments[Array.IndexOf(arguments, "--allowedTools") + 1]);
        Assert.Contains("no access to files", arguments[Array.IndexOf(arguments, "--append-system-prompt") + 1]);
    }

    [Fact]
    public async Task Ask_LetsTheAiWriteAndReadFilesOfAMountedFolderThroughTheLibraryTools()
    {
        string script = Library(
            Call("write_file", """{"path":"work/notes/hello.txt","content":"hi there"}""", "write.txt"),
            Call("read_file", """{"path":"work/notes/hello.txt"}""", "read.txt"),
            Call("edit_file", """{"path":"work/notes/hello.txt","old_string":"hi","new_string":"hello"}""", "edit.txt"),
            Call("read_file", """{"path":"/etc/hostname"}""", "outside.txt"),
            Call("write_file", """{"path":"loose.txt","content":"x"}""", "loose-result.txt"),
            Call("list_directory", "{}", "list.txt"));
        await using IPromptSession session = await Session(script, new Dictionary<string, IStore> { ["work"] = Folder("work") });

        await session.Request("work");

        Assert.Equal("hello there", await File.ReadAllTextAsync(temp.Combine("work/notes/hello.txt")));
        Assert.Contains("1\\thi there", await File.ReadAllTextAsync(temp.Combine("read.txt")));
        Assert.Contains("Edited work/notes/hello.txt", await File.ReadAllTextAsync(temp.Combine("edit.txt")));
        string outside = await File.ReadAllTextAsync(temp.Combine("outside.txt"));
        Assert.Contains("\"isError\":true", outside);
        Assert.Contains("not inside a mounted folder", outside);
        Assert.Contains("not inside a mounted folder", await File.ReadAllTextAsync(temp.Combine("loose-result.txt")));
        Assert.Contains("work/", await File.ReadAllTextAsync(temp.Combine("list.txt")));
    }

    [Fact]
    public async Task Ask_WithOtherFiles_WorksOnThemAndNotOnDisk()
    {
        FakeStore memory = new(new Dictionary<string, string> { ["a.txt"] = "from memory" });
        string script = Library(Call("read_file", """{"path":"mem/a.txt"}""", "read.txt"), Call("write_file", """{"path":"mem/b.txt","content":"written"}""", "write.txt"));
        await using IPromptSession session = await Session(script, new Dictionary<string, IStore> { ["mem"] = memory });

        await session.Request("work");

        Assert.Contains("from memory", await File.ReadAllTextAsync(temp.Combine("read.txt")));
        Assert.Equal("written", memory.Files["b.txt"]);
        Assert.False(File.Exists(temp.Combine("b.txt")));
    }

    [Fact]
    public async Task Ask_WithTwoFolders_LetsTheAiCopyBetweenThemButNotChangeTheReadOnlyOne()
    {
        Directory.CreateDirectory(temp.Combine("reference"));
        await File.WriteAllTextAsync(temp.Combine("reference/guide.md"), "the guide");
        string script = Library(
            Call("read_file", """{"path":"ref/guide.md"}""", "read.txt"),
            Call("copy_file", """{"from":"ref/guide.md","to":"work/guide-copy.md"}""", "copy.txt"),
            Call("write_file", """{"path":"ref/new.md","content":"x"}""", "refused.txt"),
            Call("list_directory", "{}", "list.txt"));
        await using IPromptSession session = await Session(script, new Dictionary<string, IStore> { ["ref"] = Folder("reference").AsReadOnly(), ["work"] = Folder("work") });

        await session.Request("work");

        Assert.Contains("the guide", await File.ReadAllTextAsync(temp.Combine("read.txt")));
        Assert.Equal("the guide", await File.ReadAllTextAsync(temp.Combine("work/guide-copy.md")));
        string refused = await File.ReadAllTextAsync(temp.Combine("refused.txt"));
        Assert.Contains("\"isError\":true", refused);
        Assert.Contains("read-only", refused);
        Assert.False(File.Exists(temp.Combine("reference/new.md")));
        string listing = await File.ReadAllTextAsync(temp.Combine("list.txt"));
        Assert.Contains("ref/", listing);
        Assert.Contains("work/", listing);
    }

    [Fact]
    public async Task Ask_TheAiCannotDeleteOrRenameAMountedFolder()
    {
        string script = Library(
            Call("delete_file", """{"path":"work"}""", "delete1.txt"),
            Call("delete_file", """{"path":"work/."}""", "delete2.txt"),
            Call("delete_file", """{"path":"work/"}""", "delete3.txt"),
            Call("move_file", """{"from":"work","to":"renamed"}""", "move1.txt"),
            Call("move_file", """{"from":"work/.","to":"work2"}""", "move2.txt"),
            Call("write_file", """{"path":"work/.","content":"x"}""", "write.txt"),
            Call("list_directory", "{}", "list.txt"));
        await using IPromptSession session = await Session(script, new Dictionary<string, IStore> { ["work"] = Folder("work") });
        await File.WriteAllTextAsync(temp.Combine("work/keep.txt"), "kept");

        await session.Request("work");

        foreach (string result in new[] { "delete1", "delete2", "delete3", "move1", "move2", "write" })
        {
            Assert.Contains("\"isError\":true", await File.ReadAllTextAsync(temp.Combine($"{result}.txt")));
        }

        Assert.Equal("kept", await File.ReadAllTextAsync(temp.Combine("work/keep.txt")));
        Assert.False(Directory.Exists(temp.Combine("renamed")));
        Assert.False(Directory.Exists(temp.Combine("work2")));
        Assert.Contains("work/", await File.ReadAllTextAsync(temp.Combine("list.txt")));
    }

    [Fact]
    public async Task Ask_WithAStoreFromMemory_TheAiWorksOnItAndTheCallerReadsTheResult()
    {
        IStore memory = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["in.txt"] = "from memory" });
        string script = Library(Call("read_file", """{"path":"mem/in.txt"}""", "read.txt"), Call("write_file", """{"path":"mem/out/result.txt","content":"made by the AI"}""", "write.txt"));
        await using IPromptSession session = await Session(script, new Dictionary<string, IStore> { ["mem"] = memory });

        await session.Request("work");

        Assert.Contains("from memory", await File.ReadAllTextAsync(temp.Combine("read.txt")));
        Assert.Equal("made by the AI", await memory.ReadText("out/result.txt"));
    }
}
