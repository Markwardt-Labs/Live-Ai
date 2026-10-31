namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ProviderTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptEngine? claude;

    private IPromptEngine? codex;

    private IPromptEngine Claude => claude ??= new ClaudeEngine(temp.Script("claude", "pwd > \"$PWD/claude-cwd.txt\"\nmkdir -p \"$CLAUDE_CONFIG_DIR/projects/p\"\n: > \"$CLAUDE_CONFIG_DIR/projects/p/claude-session.jsonl\"\nprintf '%s\\n' \"$@\" > \"$PWD/claude-args.txt\"\nprintf '%s' \"$ANTHROPIC_API_KEY\" > \"$PWD/claude-key.txt\"\nIFS= read -r line; printf '%s' \"$line\" > \"$PWD/claude-stdin.txt\"\necho '{\"type\":\"system\",\"subtype\":\"init\"}'\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"claude says hi\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"claude-session\"}'"), temp.Combine("claude-state"));

    private IPromptEngine Codex => codex ??= new CodexEngine(new FakeCodex(temp).Script(), temp.Combine("codex-state"));

    private IPromptProvider Provider(IPromptEngine engine)
        => new PromptProvider(engine, temp.Combine("data"));

    private async Task<IPromptSession> Create(IPromptEngine engine, string? apiKey = null)
        => temp.Track(await Provider(engine).Create(new PromptSessionOptions { ApiKey = apiKey }));

    private string WorkingFolder(string id)
        => Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id}");

    private Task<string> Stored()
        => File.ReadAllTextAsync(Directory.GetFiles(temp.Combine("data/Sessions")).Single());

    [Fact]
    public async Task List_ShowsTheStoredSessionsAndTheCreationTimeSurvivesTheWriteOfTheToolsSessionIdentifier()
    {
        IPromptProvider provider = Provider(Claude);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        IPromptSession fresh = temp.Track(await provider.Create());
        PromptSessionInfo notStarted = Assert.Single(await provider.List());
        await fresh.Request("hello there");

        PromptSessionInfo started = Assert.Single(await provider.List());

        Assert.False(notStarted.IsStarted);
        Assert.True(started.IsStarted);
        Assert.Equal(fresh.Id, started.Id);
        Assert.Equal(notStarted.Created, started.Created);
        Assert.InRange(started.Created!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task List_OfAnotherToolsProvider_LeavesOutTheSessions()
    {
        await Create(Claude);

        Assert.Empty(await Provider(Codex).List());
        Assert.Single(await Provider(Claude).List());
    }

    [Fact]
    public async Task AForkIsListedWithItsOwnCreationTime()
    {
        IPromptProvider provider = Provider(Claude);
        IPromptSession original = temp.Track(await provider.Create());
        await original.Request("hello there");
        await Task.Delay(20);

        IPromptSession fork = temp.Track(await original.Fork());

        IReadOnlyList<PromptSessionInfo> sessions = await provider.List();
        Assert.Equal([fork.Id, original.Id], sessions.Select(info => info.Id));
        Assert.True(sessions[0].Created > sessions[1].Created);
    }

    [Fact]
    public async Task Create_WithoutOptions_StartsAReadOnlyClaudeSessionThatRunsInAFolderOfItsOwn()
    {
        IPromptSession session = await Create(Claude);

        string reply = await session.Request("hello there");

        Assert.Equal("claude says hi", reply);
        Assert.Equal(WorkingFolder(session.Id), (await File.ReadAllTextAsync(temp.Combine("claude-cwd.txt"))).Trim());
        Assert.Equal("hello there", await temp.ReadPrompt());
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        Assert.Contains("sonnet", arguments);
        Assert.Contains("dontAsk", arguments);
        Assert.DoesNotContain("--resume", arguments);
        Assert.Equal("mcp__liveai__respond", arguments[Array.IndexOf(arguments, "--allowedTools") + 1]);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(temp.Combine("claude-key.txt")));
    }

    [Fact]
    public async Task Create_WithAWorkspacePath_SetsTheWorkspaceAndRunsTheToolInThatFolder()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        IPromptSession session = temp.Track(await Provider(Claude).Create(new PromptSessionOptions { WorkspacePath = project }));

        IDiskStore workspace = Assert.IsAssignableFrom<IDiskStore>(session.Configuration.Workspace);
        await session.Request("hello");

        Assert.Equal(project, workspace.Path);
        Assert.Equal(project, (await File.ReadAllTextAsync(temp.Combine("claude-cwd.txt"))).Trim());
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), $"markwardt.liveai-{session.Id}"));
    }

    [Fact]
    public async Task Load_SetsTheWorkspaceOfTheSessionAgainWithItsSandbox()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        string outside = temp.Combine("outside.txt");
        await File.WriteAllTextAsync(outside, "secret");
        IPromptSession created = await Provider(Claude).Create(new PromptSessionOptions { WorkspacePath = project, Sandbox = false });
        await created.Request("hi");
        await created.DisposeAsync();

        IPromptSession loaded = temp.Track(await created.Provider.Load(created.Id));

        Assert.Equal(project, Assert.IsAssignableFrom<IDiskStore>(loaded.Configuration.Workspace).Path);
        Assert.Equal("secret", await loaded.Configuration.Workspace!.ReadText(outside));
        Assert.Contains("\"sandbox\": false", await Stored());
    }

    [Fact]
    public async Task Create_WithAWorkspacePath_IsSandboxedByDefault()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        await File.WriteAllTextAsync(temp.Combine("outside.txt"), "secret");

        IPromptSession session = temp.Track(await Provider(Claude).Create(new PromptSessionOptions { WorkspacePath = project }));

        await Assert.ThrowsAnyAsync<Exception>(() => session.Configuration.Workspace!.ReadText(temp.Combine("outside.txt")));
    }

    [Fact]
    public async Task Create_WithAWorkspacePathThatDoesNotExist_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Provider(Claude).Create(new PromptSessionOptions { WorkspacePath = temp.Combine("missing") }));

        Assert.False(Directory.Exists(temp.Combine("data/Sessions")) && Directory.GetFiles(temp.Combine("data/Sessions")).Length > 0);
    }

    [Fact]
    public async Task Delete_OfAWorkspaceSession_LeavesTheFolderAndItsFiles()
    {
        string project = Directory.CreateDirectory(temp.Combine("project")).FullName;
        IPromptProvider provider = Provider(Claude);
        string marker = Path.Combine(project, "keep.txt");
        await File.WriteAllTextAsync(marker, "mine");
        IPromptSession session = await provider.Create(new PromptSessionOptions { WorkspacePath = project });
        await session.Request("hi");
        string id = session.Id;
        await session.DisposeAsync();

        Assert.True(await provider.Delete(id));

        Assert.True(Directory.Exists(project));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task Create_StartsWithBalancedCapabilityMediumReasoningAndNothingElse()
    {

        IPromptSession created = await Create(Claude);
        IPromptSession loaded = await created.Provider.Load(created.Id);

        foreach (IPromptSession session in new[] { created, loaded })
        {
            Assert.Equal(PromptCapability.Balanced, session.Configuration.Capability);
            Assert.Equal(PromptReasoning.Medium, session.Configuration.Reasoning);
            Assert.False(session.Configuration.IsPersistent);
            Assert.Empty(session.Configuration.Tools);
            Assert.Empty(session.Configuration.Mounts);
        }
    }

    [Fact]
    public async Task Create_LinksTheLibraryIdentifierToTheProviderSession()
    {
        IPromptSession session = await Create(Claude);

        Assert.Contains("\"aiSessionId\": null", await Stored());
        await session.Request("hi");

        Assert.NotEqual(Guid.Empty, Guid.Parse(session.Id));
        string stored = await Stored();
        Assert.Contains("\"aiSessionId\": \"claude-session\"", stored);
        Assert.Contains(session.Id, stored);
    }

    [Fact]
    public async Task Load_ResumesTheProviderSessionAndStartsWithTheDefaultSettingsWhateverTheOriginalHad()
    {
        Directory.CreateDirectory(temp.Combine("project"));
        IPromptSession created = await Create(Claude);
        created.Configuration.Capability = PromptCapability.Max;
        created.Configuration.Reasoning = PromptReasoning.High;
        created.Configuration.EnableShell = true;
        created.Configuration.Instructions = "Be brief.";
        created.Configuration.Mounts["project"] = IStore.FromDisk(temp.Combine("project"));
        created.Configuration.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp");
        await created.Request("first");
        await created.DisposeAsync();

        IPromptSession loaded = await created.Provider.Load(created.Id);

        Assert.Equal(created.Id, loaded.Id);
        Assert.Same(created.Provider, loaded.Provider);
        Assert.Equal((PromptCapability.Balanced, PromptReasoning.Medium, false, false, null), (loaded.Configuration.Capability, loaded.Configuration.Reasoning, loaded.Configuration.IsPersistent, loaded.Configuration.EnableShell, loaded.Configuration.Instructions));
        Assert.Empty(loaded.Configuration.Mounts);
        Assert.Empty(loaded.Configuration.McpServers);
        loaded.Configuration.Capability = PromptCapability.Max;
        loaded.Configuration.Mounts["files"] = new FakeStore();
        Assert.Equal("claude says hi", await loaded.Request("second"));
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        Assert.Equal("claude-session", arguments[Array.IndexOf(arguments, "--resume") + 1]);
        Assert.Contains("opus", arguments);
        Assert.Equal("second", await temp.ReadPrompt());
        string stored = await Stored();
        Assert.DoesNotContain("project", stored);
        Assert.DoesNotContain("Be brief", stored);
        Assert.DoesNotContain("opus", stored);
    }

    [Fact]
    public async Task SettingsChangedOnASession_ApplyToTheNextPrompt()
    {
        IPromptSession session = await Create(Claude);
        session.Configuration.Mounts["files"] = new FakeStore();
        await session.Request("first");
        string[] first = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        Assert.Contains("mcp__liveai__write_file", first[Array.IndexOf(first, "--allowedTools") + 1].Split(','));
        Assert.Contains("sonnet", first);

        session.Configuration.Capability = PromptCapability.Max;
        session.Configuration.Reasoning = PromptReasoning.High;
        await session.Request("second");

        Assert.Contains("opus", await File.ReadAllLinesAsync(temp.Combine("claude-args.txt")));
    }

    [Fact]
    public async Task Load_BeforeTheFirstPrompt_StartsTheProviderSessionFresh()
    {
        IPromptSession created = await Create(Codex);

        IPromptSession loaded = await created.Provider.Load(created.Id);
        await loaded.Request("hi");

        Assert.Equal(["thread/start"], (await new FakeCodex(temp).Requests()).Select(request => request.GetProperty("method").GetString()).Where(method => method!.StartsWith("thread/")));
        Assert.Contains("\"aiSessionId\": \"codex-thread\"", await Stored());
    }

    [Fact]
    public async Task Codex_ResumesTheThreadAndPassesTheApiKey()
    {
        IPromptSession created = await Create(Codex, "first-key");
        Assert.Equal("codex says hi", await created.Request("one"));
        Assert.Equal("first-key", await File.ReadAllTextAsync(temp.Combine("codex-key.txt")));

        IPromptSession loaded = await created.Provider.Load(created.Id, new PromptSessionOptions { ApiKey = "second-key" });
        await loaded.Request("two");

        Assert.Equal(["app-server"], (await File.ReadAllLinesAsync(temp.Combine("codex-args.txt"))).Take(1));
        JsonElement resume = Assert.Single(await new FakeCodex(temp).Parameters("thread/resume"));
        Assert.Equal("codex-thread", resume.GetProperty("threadId").GetString());
        Assert.Equal("second-key", await File.ReadAllTextAsync(temp.Combine("codex-key.txt")));
    }

    [Fact]
    public async Task Create_NeverStoresTheApiKey()
    {
        IPromptSession session = await Create(Claude, "very-secret");
        await session.Request("hi");

        string stored = await File.ReadAllTextAsync(Directory.GetFiles(temp.Combine("data/Sessions")).Single());

        Assert.DoesNotContain("very-secret", stored);
    }

    [Fact]
    public async Task Load_MakesTheSessionsFolderAgainWhenItWasRemoved()
    {
        IPromptSession created = await Create(Claude);
        Directory.Delete(WorkingFolder(created.Id), true);

        IPromptSession loaded = await created.Provider.Load(created.Id);
        await loaded.Request("hi");

        Assert.Equal(WorkingFolder(created.Id), (await File.ReadAllTextAsync(temp.Combine("claude-cwd.txt"))).Trim());
    }

    [Fact]
    public async Task Load_WhenTheProviderDeletedItsSession_ThrowsAndKeepsTheLink()
    {
        IPromptSession created = await Create(Claude);
        await created.Request("hi");
        string file = Directory.GetFiles(temp.Combine("data/Sessions")).Single();
        File.Delete(temp.Combine("claude-state/projects/p/claude-session.jsonl"));

        PromptSessionNotFoundException exception = await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => created.Provider.Load(created.Id));

        Assert.Equal(created.Id, exception.Id);
        Assert.True(File.Exists(file));
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => created.Provider.Load(created.Id));
    }

    [Fact]
    public async Task Load_ForAnUnknownIdentifier_Throws()
        => await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => Provider(Claude).Load(Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Send_WhenTheToolIsMissing_Throws()
    {
        IPromptSession session = await Create(new ClaudeEngine(temp.Combine("no-such-tool"), temp.Combine("claude-state")));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptToolStartException>(() => session.Request("hi"));

        Assert.Contains("Claude Code could not be started", exception.Message);
    }

    [Fact]
    public async Task Send_WhenTheAiFails_Throws()
    {
        IPromptSession session = await Create(new ClaudeEngine(temp.Script("failing", "IFS= read -r line\necho '{\"type\":\"result\",\"is_error\":true,\"result\":\"Invalid API key\"}'\nexit 1"), temp.Combine("claude-state")));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("hi"));

        Assert.Equal("Invalid API key", exception.Message);
        Assert.Contains("\"aiSessionId\": null", await Stored());
    }

    [Fact]
    public async Task Request_ReportsRepliesOnOutputAsTheyArrive()
    {
        IPromptSession session = await Create(new CodexEngine(new FakeCodex(temp).Script("slow", turn: "echo \"{\\\"method\\\":\\\"item/completed\\\",\\\"params\\\":{\\\"turnId\\\":\\\"$turn\\\",\\\"item\\\":{\\\"type\\\":\\\"agentMessage\\\",\\\"id\\\":\\\"m\\\",\\\"text\\\":\\\"early\\\"}}}\"; sleep 300"), temp.Combine("codex-state")));
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));

        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);

        Task<string> asking = session.Request("hi", cancellation: cancellation.Token);
        for (int attempt = 0; attempt < 1000 && !recorder.Values.OfType<PromptMessageEvent>().Any(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("early", recorder.Values.OfType<PromptMessageEvent>().First().Text);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
    }
}
