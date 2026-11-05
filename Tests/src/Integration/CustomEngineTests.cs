namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class CustomEngineTests : IDisposable
{
    private readonly TempFolder temp = new();

    private IPromptEngine engine = null!;

    public void Dispose()
        => temp.Dispose();

    private string Tool(string body = "", string name = "tool")
        => temp.Script(name, $"printf '%s\\n' \"$@\" >> \"$PWD/args.txt\"\nprintf '%s|%s|%s\\n' \"$LIVEAI_TOOLS_TOKEN\" \"$SCRIPT_API_KEY\" \"$SCRIPT_INSTRUCTIONS\" >> \"$PWD/env.txt\"\nn=$(wc -l < \"$PWD/env.txt\")\ncat > \"$PWD/stdin-$n.txt\"\n{(body.Length > 0 ? body : "echo \"SESSION custom-$n\"\necho \"REPLY hello $n\"\necho DONE")}");

    private (IPromptProvider Provider, ScriptEngine Script) Make(string? body = null, bool images = false)
    {
        ScriptEngine script = new(Tool(body ?? string.Empty)) { TakesImages = images };
        engine = script;
        return (new PromptProvider(engine, temp.Combine("data")), script);
    }

    [Fact]
    public async Task AProviderWithALogger_ReportsWhatItsSessionsDo()
    {
        ListLogger logger = new();
        engine = new ScriptEngine(Tool(string.Empty));
        PromptProvider provider = new(engine, temp.Combine("data"), logger);
        await using IPromptSession session = await Create(provider);

        await session.Request("one");

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("is now Responding"));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("started the tool server"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("hello") || entry.Message.Contains("one"));
    }

    private async Task<IPromptSession> Create(IPromptProvider provider, string? apiKey = null)
        => temp.Track(await provider.Create(TestModels.With(apiKey)));

    [Fact]
    public async Task ASessionOnACustomProvider_RunsPromptsAndResumesWithTheSessionTheToolReported()
    {
        (IPromptProvider provider, ScriptEngine script) = Make();
        await using IPromptSession session = await Create(provider, "secret-key");

        string first = await session.Request("one");
        string second = await session.Request("two");

        Assert.Equal("hello 1", first);
        Assert.Equal("hello 2", second);
        Assert.Same(provider, session.Provider);
        Assert.Null(script.Starts[0].AiSessionId);
        Assert.Equal("custom-1", script.Starts[1].AiSessionId);
        Assert.Equal("one", await File.ReadAllTextAsync(temp.Combine("stdin-1.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(temp.Combine("stdin-2.txt")));
        Assert.All(script.Starts, start => Assert.Equal("secret-key", start.ApiKey));
        Assert.Equal("test-model", script.Starts[0].Model);
        Assert.Equal("medium", script.Starts[0].Settings["effort"]);
        Assert.Contains("You have no access to files", script.Starts[0].Instructions);
    }

    [Fact]
    public async Task TheProviderIsStoredByNameAndFoundAgainWhenLoaded()
    {
        (IPromptProvider provider, _) = Make();
        IPromptSession session = await Create(provider);
        await session.Request("one");
        string id = session.Id;
        await session.DisposeAsync();

        await using IPromptSession loaded = temp.Track(await provider.Load(id));

        Assert.Same(provider, loaded.Provider);
        Assert.Contains("\"provider\": \"Script Tool\"", await File.ReadAllTextAsync(temp.Combine($"data/Sessions/{id}.json")));
        Assert.Equal("hello 2", await loaded.Request("two"));
    }

    [Fact]
    public async Task ALoadedSessionAcceptsAnotherProviderOfAnEngineOfTheSameName()
    {
        (IPromptProvider provider, _) = Make();
        IPromptSession session = await Create(provider);
        await session.Request("one");
        await session.DisposeAsync();
        IPromptProvider same = new PromptProvider(new ScriptEngine(Tool()), temp.Combine("data"));

        await using IPromptSession loaded = temp.Track(await same.Load(session.Id));

        Assert.Same(same, loaded.Provider);
        Assert.Equal("hello 2", await loaded.Request("two"));
    }

    [Fact]
    public async Task ACustomSession_CannotBeLoadedOrDeletedByAProviderOfAnotherEngine()
    {
        (IPromptProvider provider, _) = Make();
        IPromptSession session = await Create(provider);
        await session.DisposeAsync();

        IPromptProvider codex = new PromptProvider(new CodexEngine(), temp.Combine("data"));
        IPromptProvider claude = new PromptProvider(new ClaudeEngine(), temp.Combine("data"));

        ArgumentException wrong = await Assert.ThrowsAsync<ArgumentException>(() => codex.Load(session.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => claude.Delete(session.Id));

        Assert.Contains("Script Tool", wrong.Message);
        Assert.True(File.Exists(temp.Combine($"data/Sessions/{session.Id}.json")));
    }

    [Fact]
    public async Task TheLibraryServerExternalServersSchemaAndInstructionsAreHandedOver()
    {
        (IPromptProvider provider, ScriptEngine script) = Make("echo \"REPLY {\\\"score\\\":3}\"\necho DONE");
        await using IPromptSession session = await Create(provider);
        session.Configuration.ModelSettings["effort"] = "high";
        session.Configuration.Instructions = "Be brief.";
        session.Configuration.Tools.Add(PromptTool.Create<Question, string>("ask", "asks", (_, _) => Task.FromResult("a")));
        session.Configuration.McpServers["db"] = PromptMcpServer.Http("https://example.com/mcp", "DB_TOKEN");
        session.Configuration.McpServers["git"] = PromptMcpServer.Stdio("git-mcp", "--flag") with { EnvironmentVariables = new Dictionary<string, string> { ["K"] = "v" } };

        Answer answer = await session.Request<Answer>("rate").ContinueWith(task => task.IsFaulted ? new Answer("failed", 0) : task.Result);
        PromptEngineStart start = script.Starts.Single();

        Assert.Equal(("test-model", "high"), (start.Model, start.Settings["effort"]));
        Assert.EndsWith("Instructions from the user:\nBe brief.", start.Instructions);
        Assert.Contains("db, git", start.Instructions);
        Assert.Equal("object", start.Schema!.Value.GetProperty("type").GetString());
        Assert.Equal(["liveai", "db", "git"], start.Servers.Select(server => server.Name));
        PromptEngineServer own = start.Servers[0];
        Assert.StartsWith("http://127.0.0.1:", own.Url);
        Assert.Equal("LIVEAI_TOOLS_TOKEN", own.BearerTokenVariable);
        Assert.Equal(["current_time", "tool_search", "delegate", "ask", "respond"], own.Tools);
        Assert.Equal(64, own.BearerToken!.Length);
        Assert.Equal(("https://example.com/mcp", "DB_TOKEN", (string?)null), (start.Servers[1].Url, start.Servers[1].BearerTokenVariable, start.Servers[1].BearerToken));
        Assert.Equal("git-mcp", start.Servers[2].Command);
        Assert.Equal(["--flag"], start.Servers[2].Arguments);
        Assert.Equal("v", start.Servers[2].EnvironmentVariables["K"]);
        Assert.StartsWith(own.BearerToken, (await File.ReadAllTextAsync(temp.Combine("env.txt"))).TrimEnd());
        Assert.Equal(new Answer("failed", 0), answer);
    }

    [Fact]
    public async Task FilesTheToolAcceptsAreWrittenForThePromptAndTheRestIsFoldedIntoItOrRefused()
    {
        (IPromptProvider provider, ScriptEngine script) = Make(images: true);
        await using IPromptSession session = await Create(provider);
        PromptAttachment image = new() { Name = "shot.png", MediaType = "image/png", Content = new byte[] { 1, 2, 3 } };
        PromptAttachment notes = new() { Name = "notes.txt", MediaType = "text/plain", Content = "remember"u8.ToArray() };
        PromptAttachment pdf = new() { Name = "a.pdf", MediaType = "application/pdf", Content = new byte[] { 9 } };

        await session.Request("look", new PromptRequestOptions { Attachments = [image, notes] });
        await Assert.ThrowsAsync<NotSupportedException>(() => session.Request("look", new PromptRequestOptions { Attachments = [pdf] }));

        PromptEngineStart start = script.Starts.Single();
        Assert.EndsWith(".png", Assert.Single(start.Files));
        Assert.Contains("remember", await File.ReadAllTextAsync(temp.Combine("stdin-1.txt")));
        Assert.Contains("image/png", script.Accepted);
        Assert.False(Directory.Exists(Path.GetDirectoryName(start.Files[0])));
    }

    [Fact]
    public async Task WhatTheProviderDoesNotOfferStaysUnsupported()
    {
        (IPromptProvider provider, _) = Make();
        await using IPromptSession session = await Create(provider);
        await session.Request("one");
        session.Configuration.IsPersistent = true;

        await session.Request("two");

        Assert.Empty(await session.GetHistory().Take(10).ToListAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => session.Fork());
        Assert.Empty(await engine.GetWindows());
        Assert.Equal(2, File.ReadAllLines(temp.Combine("env.txt")).Length);
    }

    [Fact]
    public async Task ADeletedSessionHasItsConversationDeletedThroughTheProvider()
    {
        (IPromptProvider provider, ScriptEngine script) = Make();
        IPromptSession session = await Create(provider);
        await session.Request("one");
        string id = session.Id;
        await session.DisposeAsync();

        Assert.True(await provider.Delete(id));

        Assert.Equal(["custom-1"], script.Deleted);
        await Assert.ThrowsAsync<PromptSessionNotFoundException>(() => provider.Load(id));
    }

    [Fact]
    public async Task Send_GivesTheToolInputWhoseOutputIsOnTheOutputOfTheSession()
    {
        (IPromptProvider provider, _) = Make();
        await using IPromptSession session = await Create(provider);
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);

        await session.Send("hello there");
        for (int attempt = 0; attempt < 500 && recorder.Values.OfType<PromptStateEvent>().Count() < 2; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal([PromptSessionState.Working, PromptSessionState.Waiting], recorder.Values.OfType<PromptStateEvent>().Select(change => change.State));
        Assert.Equal(["hello 1"], recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text));
        Assert.Equal("hello there", await File.ReadAllTextAsync(temp.Combine("stdin-1.txt")));
    }

    [Fact]
    public async Task AForkRequest_IsAskedInASessionOfItsOwnThatLeavesNothingBehind()
    {
        (IPromptProvider provider, _) = Make();
        await using IPromptSession session = await Create(provider);
        Recorder<PromptEvent> recorder = new();
        session.Output.Subscribe(recorder);

        string reply = await session.Request("side question", new PromptRequestOptions { Mode = PromptRequestMode.Fork });

        Assert.Equal("hello 1", reply);
        Assert.Empty(recorder.Values);
        Assert.Equal(PromptSessionState.Waiting, session.State);
        Assert.Equal([session.Id], Directory.GetFiles(temp.Combine("data/Sessions")).Select(file => Path.GetFileNameWithoutExtension(file)));
    }

    [Fact]
    public async Task AFailureOfTheToolSurfacesAsTheFailureOfThePrompt()
    {
        (IPromptProvider provider, _) = Make("echo \"FAIL out of quota\"");
        await using IPromptSession session = await Create(provider);

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        Assert.Equal("out of quota", exception.Message);
    }

    [Fact]
    public async Task TheTesterRunsTheProviderToo()
    {
        ScriptEngine script = new(Tool()) { Models = [TestModels.Model] };
        ScriptEngine broken = new(Tool("echo \"FAIL not signed in\"", "broken"));

        PromptTestResult ok = await new PromptProvider(script, temp.Combine("data")).Test("key");
        PromptTestResult bad = await new PromptProvider(broken, temp.Combine("data")).Test();

        Assert.True(ok.IsAvailable);
        Assert.Equal("key", script.Starts.Single().ApiKey);
        Assert.Equal(("test-model", "low"), (script.Starts.Single().Model, script.Starts.Single().Settings["effort"]));
        Assert.False(bad.IsAvailable);
        Assert.Equal("not signed in", bad.Error);
    }
}
