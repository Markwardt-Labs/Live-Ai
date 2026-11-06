namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class OpenCodeSessionTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly FakeOpenCodeServer server = new();

    private readonly FakeOpenCode fake;

    private readonly FakeModelServer ollama = new FakeModelServer().Answer("/api/tags", """{ "models": [{ "name": "gemma4:26b", "capabilities": ["completion", "tools"] }] }""");

    public OpenCodeSessionTests()
        => fake = new FakeOpenCode(temp);

    public Task InitializeAsync()
        => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        ollama.Dispose();
        temp.Dispose();
    }

    private OpenCodeEngine Engine(IEnumerable<OpenCodeModelProvider>? providers = null, string? export = null, string? fork = null, string? command = null)
        => new(command ?? fake.Script(server, export: export, fork: fork), [], providers) { ConnectWait = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromMilliseconds(20) };

    private IPromptProvider Provider(OpenCodeEngine engine)
        => TestProviders.Make(engine, temp.Combine("data"));

    private async Task<IPromptSession> Create(string? export = null, string? fork = null)
        => await Provider(Engine(null, export, fork)).Create(new PromptProviderOptions { WorkspacePath = temp.Path });

    private IReadOnlyList<FakeOpenCodeServer.Step> Turn(params FakeOpenCodeServer.Step[] steps)
        => [FakeOpenCodeServer.Step.Event("session.execution.started", new { }), .. steps, FakeOpenCodeServer.Step.Event("session.execution.succeeded", new { })];

    private FakeOpenCodeServer.Step Text(string text)
        => FakeOpenCodeServer.Step.Event("session.text.ended", new { ordinal = 0, text });

    private FakeOpenCodeServer.Step Tokens(int input = 10, int output = 5)
        => FakeOpenCodeServer.Step.Event("session.step.ended", new { finish = "stop", tokens = new { input, output, reasoning = 0, cache = new { read = 0, write = 0 } }, cost = 0 });

    private async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }
    }

    private bool Prompted(int count)
        => server.Requests.Count(request => request.Method == "POST" && request.Path.EndsWith("/prompt", StringComparison.Ordinal)) >= count;

    [Fact]
    public async Task AReply_ComesAsDeltasThenTheMessageThenTheUsageOfThePrompt()
    {
        server.OnPrompt = _ => server.Reply("hello world");
        await using IPromptSession session = await Create();

        List<PromptEvent> events = await session.EventList("hi");

        Assert.Equal<PromptEvent>([new PromptDeltaEvent { Text = "hello" }, new PromptDeltaEvent { Text = " world" }, new PromptMessageEvent { Text = "hello world" }, new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 10, OutputTokens = 5 } }], events);
        Assert.Equal("hi", Assert.Single(server.Bodies("POST", "/api/session/ses_fake/prompt")).GetProperty("text").GetString());
    }

    [Fact]
    public async Task TheServerIsStartedWithAPasswordOnAFreePortAndEveryRequestUsesItInTheFolderOfTheSession()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create();

        await session.Request("hi");

        Assert.Equal(["serve", "--stdio", "--port", "0", "--hostname", "127.0.0.1"], await fake.Arguments("serve"));
        string password = await fake.Password();
        Assert.True(password.Length >= 32);
        Assert.All(server.Requests.Where(request => request.Path != "/api/event"), request =>
        {
            Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}"))}", request.Authorization);
            Assert.Equal(temp.Path, request.Directory);
        });
    }

    [Fact]
    public async Task AfterAMove_TheNextPromptContinuesTheSameSessionOnAServerStartedInTheNewFolder()
    {
        server.OnPrompt = _ => server.Reply("ok");
        string moved = Directory.CreateDirectory(temp.Combine("moved")).FullName;
        await using IPromptSession session = await Create();
        await session.Request("one");

        await session.Move(moved);
        await session.Request("two");

        Assert.Single(server.Requests, request => request is { Method: "POST", Path: "/api/session" });
        Assert.Equal(2, server.Bodies("POST", "/api/session/ses_fake/prompt").Count);
        Assert.Equal(moved, server.Requests.Where(request => request.Path != "/api/event").Last().Directory);
        Assert.Equal(2, (await File.ReadAllLinesAsync(temp.Combine("pids.txt"))).Length);
    }

    [Fact]
    public async Task TheServerAndTheSessionAreOpenedOnceAndEachPromptAfterItGoesToTheSameSession()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create();

        await session.Request("one");
        await session.Request("two");

        Assert.Single(server.Requests, request => request is { Method: "POST", Path: "/api/session" });
        Assert.Equal(2, server.Bodies("POST", "/api/session/ses_fake/prompt").Count);
        Assert.Single(await File.ReadAllLinesAsync(temp.Combine("pids.txt")));
    }

    [Fact]
    public async Task ASessionThatWasStoredIsLookedUpAndNotCreatedAgain()
    {
        server.OnPrompt = _ => server.Reply("ok");
        IPromptProvider provider = Provider(Engine());
        IPromptSession first = await provider.Create(new PromptProviderOptions { WorkspacePath = temp.Path });
        await first.Request("one");
        string id = first.Id;
        await first.DisposeAsync();

        await using IPromptSession loaded = await provider.Load(id);
        await loaded.Request("two");

        Assert.Single(server.Requests, request => request is { Method: "POST", Path: "/api/session" });
        Assert.Single(server.Requests, request => request is { Method: "GET", Path: "/api/session/ses_fake" });
    }

    [Fact]
    public async Task ASessionThatTheToolDoesNotKnowFailsWithItsMessage()
    {
        server.OnPrompt = _ => server.Reply("ok");
        IPromptProvider provider = Provider(Engine());
        IPromptSession first = await provider.Create(new PromptProviderOptions { WorkspacePath = temp.Path });
        await first.Request("one");
        string id = first.Id;
        await first.DisposeAsync();
        server.SessionId = "ses_other";

        await using IPromptSession loaded = await provider.Load(id);
        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => loaded.Request("two"));

        Assert.Contains("Session not found", exception.Message);
    }

    [Fact]
    public async Task TheToolsOfThePromptComeAsACallAndItsResultInTheOrderTheyHappened()
    {
        server.OnPrompt = _ => Turn(
            FakeOpenCodeServer.Step.Event("session.tool.input.started", new { id = "c1", name = "liveai_read_file" }),
            FakeOpenCodeServer.Step.Event("session.tool.called", new { id = "c1", input = new { path = "a.txt" }, executed = false }),
            FakeOpenCodeServer.Step.Event("session.tool.success", new { id = "c1", content = new[] { new { type = "text", text = "file text" } } }),
            FakeOpenCodeServer.Step.Event("session.tool.input.started", new { id = "c2", name = "shell_run" }),
            FakeOpenCodeServer.Step.Event("session.tool.called", new { id = "c2", input = new { } }),
            FakeOpenCodeServer.Step.Event("session.tool.failed", new { id = "c2", error = new { type = "tool.execution", message = "it broke" } }),
            Tokens());
        await using IPromptSession session = await Create();

        List<PromptEvent> events = await session.EventList("go");

        Assert.Equal<PromptEvent>(
        [
            new PromptToolCallEvent { Id = "c1", Server = "liveai", Name = "read_file", Input = """{"path":"a.txt"}""" },
            new PromptToolResultEvent { Id = "c1", Output = "file text", IsError = false },
            new PromptToolCallEvent { Id = "c2", Server = null, Name = "shell_run", Input = "{}" },
            new PromptToolResultEvent { Id = "c2", Output = "it broke", IsError = true },
            new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 10, OutputTokens = 5 } }
        ], events);
    }

    [Fact]
    public async Task TextWrittenBeforeAToolCallIsItsOwnMessage()
    {
        server.OnPrompt = _ => Turn(Text("Let me look."), FakeOpenCodeServer.Step.Event("session.tool.called", new { id = "c1", input = new { } }), FakeOpenCodeServer.Step.Event("session.tool.success", new { id = "c1", content = Array.Empty<object>() }), Text("Found it."));
        await using IPromptSession session = await Create();

        List<PromptEvent> events = await session.EventList("go");

        Assert.Equal(["Let me look.", "Found it."], events.OfType<PromptMessageEvent>().Select(message => message.Text));
    }

    [Fact]
    public async Task TheUsageOfThePromptIsTheSumOfItsStepsAndTheContextIsWhatTheLastOneUsedOfTheWindow()
    {
        server.OnPrompt = _ => Turn(Tokens(100, 7), Tokens(120, 9));
        await using IPromptSession session = await Create();

        List<PromptEvent> events = await session.EventList("go");
        PromptContext context = await session.GetContext();

        Assert.Equal(new PromptUsage { InputTokens = 220, OutputTokens = 16 }, Assert.IsType<PromptUsageEvent>(events[^1]).Usage);
        Assert.Equal((129L, 32000L), (context.Tokens, context.Limit));
    }

    [Fact]
    public async Task AnExecutionThatFailsFailsTheRequestWithTheMessageOfTheError()
    {
        server.OnPrompt = _ => [FakeOpenCodeServer.Step.Event("session.execution.started", new { }), FakeOpenCodeServer.Step.Event("session.execution.failed", new { error = new { type = "provider.invalid-request", message = "the model is unreachable" } })];
        await using IPromptSession session = await Create();

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        Assert.Equal("the model is unreachable", exception.Message);
    }

    [Fact]
    public async Task AModelThatKeepsRetryingFailsTheRequestAtTheFirstRetryAndStopsTheWork()
    {
        server.OnPrompt = _ => [FakeOpenCodeServer.Step.Event("session.execution.started", new { }), FakeOpenCodeServer.Step.Event("session.retry.scheduled", new { attempt = 2, error = new { type = "provider.internal", message = "model exploded", status = 500 } }), FakeOpenCodeServer.Step.Interrupted()];
        await using IPromptSession session = await Create();

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        Assert.Equal("model exploded", exception.Message);
        Assert.Single(server.Requests, request => request is { Method: "POST", Path: "/api/session/ses_fake/interrupt" });
    }

    [Fact]
    public async Task TheModelAndTheVariantFollowTheCapabilityAndTheEffortAndAreSentOnlyWhenTheyChange()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create();
        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Effort = PromptEffort.Max;

        await session.Request("one");
        await session.Request("two");
        session.Configuration.Capability = PromptCapability.Balanced;
        session.Configuration.Effort = PromptEffort.Low;
        await session.Request("three");

        Assert.Equal(["fake/big#max", "fake/model#low"], server.Bodies("POST", "/api/session/ses_fake/model").Select(body => $"{body.GetProperty("model").GetProperty("providerID").GetString()}/{body.GetProperty("model").GetProperty("id").GetString()}#{(body.GetProperty("model").TryGetProperty("variant", out JsonElement variant) ? variant.GetString() : string.Empty)}"));
    }

    [Fact]
    public async Task TheLocalModelOfAnOllamaServerGetsItsProviderAndTheContextSizeFromItsSetting()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await LocalSession(16384);

        await session.Request("go");

        JsonElement config = await fake.Config();
        string providerId = new LocalServer(ollama.Api).ProviderId;
        Assert.Equal($"{providerId}/gemma4:26b", config.GetProperty("model").GetString());
        JsonElement description = config.GetProperty("provider").GetProperty(providerId).GetProperty("models").GetProperty("gemma4:26b");
        Assert.Equal((16384, 8192), (description.GetProperty("limit").GetProperty("context").GetInt32(), description.GetProperty("limit").GetProperty("output").GetInt32()));
        Assert.Equal(ollama.Api, config.GetProperty("provider").GetProperty(providerId).GetProperty("options").GetProperty("baseURL").GetString());
    }

    [Fact]
    public async Task TwoSessionsWithTheSameOptionsExceptTheContextSizeEachRunWithTheirOwnSize()
    {
        server.OnPrompt = _ => server.Reply("ok");
        Prompter prompter = TestProviders.MakePrompter(options => new OpenCodeEngine(fake.Script(server), [ollama.Api], null, ((OllamaSessionOptions)options).Models, ((OllamaSessionOptions)options).Context) { ConnectWait = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromMilliseconds(20) }, temp.Combine("data"));
        OllamaSessionOptions small = new() { Models = PromptCapabilityModels.All("gemma4:26b"), Context = 8192, WorkspacePath = temp.Path };
        await using IPromptSession first = await prompter.Create(small);
        await using IPromptSession second = await prompter.Create(small with { Context = 16384 });

        string providerId = new LocalServer(ollama.Api).ProviderId;
        await first.Request("one");
        int firstSize = (await fake.Config()).GetProperty("provider").GetProperty(providerId).GetProperty("models").GetProperty("gemma4:26b").GetProperty("limit").GetProperty("context").GetInt32();
        await second.Request("two");
        int secondSize = (await fake.Config()).GetProperty("provider").GetProperty(providerId).GetProperty("models").GetProperty("gemma4:26b").GetProperty("limit").GetProperty("context").GetInt32();

        Assert.Equal((8192, 16384), (firstSize, secondSize));
        Assert.Equal(2, (await File.ReadAllLinesAsync(temp.Combine("pids.txt"))).Length);
        Assert.Equal(8192, (await StoredContext(first)));
        Assert.Equal(16384, (await StoredContext(second)));
    }

    private async Task<int> StoredContext(IPromptSession session)
        => JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine($"data/Sessions/{session.Id}.json"))).RootElement.GetProperty("options").GetProperty("context").GetInt32();

    private async Task<IPromptSession> LocalSession(int context)
        => await TestProviders.Make(new OpenCodeEngine(fake.Script(server), [ollama.Api], null, PromptCapabilityModels.All("gemma4:26b"), context) { ConnectWait = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromMilliseconds(20) }, temp.Combine("data")).Create(new PromptProviderOptions { WorkspacePath = temp.Path });

    [Fact]
    public async Task AnAnswerToASchemaThatIsNotInACodeFenceIsKeptAsItIs()
    {
        server.OnPrompt = _ => Turn(Text("{\"summary\":\"fine\",\"score\":4}"), Tokens());
        await using IPromptSession session = await Create();

        Assert.Equal(new Answer("fine", 4), await session.Request<Answer>("rate it"));
    }

    [Fact]
    public async Task AStepThatEndsWithoutTokenCountsAddsNoUsage()
    {
        server.OnPrompt = _ => Turn(Text("ok"), FakeOpenCodeServer.Step.Event("session.step.ended", new { finish = "stop" }));
        await using IPromptSession session = await Create();

        await session.Request("go");

        Assert.Equal(new PromptUsage(), session.Usage);
    }

    [Fact]
    public async Task AnImageIsSentAsADataUriAndNotesComeBeforeThePrompt()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create();
        PromptAttachment image = new() { Name = "pixel.png", MediaType = "image/png", Content = new byte[] { 1, 2, 3 } };

        await session.Request("what is this", new PromptRequestOptions { Attachments = [image], Notes = ["the user is in a hurry"] });

        JsonElement body = Assert.Single(server.Bodies("POST", "/api/session/ses_fake/prompt"));
        Assert.StartsWith("Note from the host application: the user is in a hurry", body.GetProperty("text").GetString());
        JsonElement file = Assert.Single(body.GetProperty("files").EnumerateArray());
        Assert.Equal(($"data:image/png;base64,{Convert.ToBase64String([1, 2, 3])}", "pixel.png"), (file.GetProperty("uri").GetString(), file.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task InterruptStopsThePromptAndItEndsWithAnInterruption()
    {
        server.OnPrompt = _ => [FakeOpenCodeServer.Step.Event("session.execution.started", new { }), FakeOpenCodeServer.Step.Interrupted()];
        await using IPromptSession session = await Create();
        Recorder<PromptEvent> recorder = new();
        using IDisposable watch = session.Subscribe(recorder);

        Task<string> asking = session.Request("slow");
        await WaitFor(() => Prompted(1));
        bool interrupted = await session.Interrupt();
        string text = await asking.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(interrupted);
        Assert.Equal(string.Empty, text);
        Assert.Single(server.Requests, request => request is { Method: "POST", Path: "/api/session/ses_fake/interrupt" });
        await recorder.Until(values => values.Any(output => output is PromptInterruptedEvent));
    }

    [Fact]
    public async Task Send_ToAPromptThatRuns_IsSteeredIntoItAndDoesNotStartAnother()
    {
        server.OnPrompt = _ => [FakeOpenCodeServer.Step.Event("session.execution.started", new { }), FakeOpenCodeServer.Step.Steered("session.text.ended", new { ordinal = 0, text = "both done" }), Tokens(), FakeOpenCodeServer.Step.Event("session.execution.succeeded", new { })];
        await using IPromptSession session = await Create();
        Recorder<PromptEvent> recorder = new();
        using IDisposable watch = session.Subscribe(recorder);

        await session.Send("first");
        await WaitFor(() => Prompted(1));
        await session.Send("second", new PromptSendOptions { Notes = ["be quick"] });
        await WaitFor(() => session.State == PromptSessionState.Waiting);

        IReadOnlyList<JsonElement> prompts = server.Bodies("POST", "/api/session/ses_fake/prompt");
        Assert.Equal(2, prompts.Count);
        Assert.Equal("first", prompts[0].GetProperty("text").GetString());
        Assert.Equal("steer", prompts[1].GetProperty("delivery").GetString());
        Assert.EndsWith("second", prompts[1].GetProperty("text").GetString());
        Assert.Contains("be quick", prompts[1].GetProperty("text").GetString());
        await recorder.Until(values => values.OfType<PromptMessageEvent>().Any());
        Assert.Equal("both done", recorder.Values.OfType<PromptMessageEvent>().Single().Text);
    }

    [Fact]
    public async Task Compact_AsksTheServerToCompactAndWaitsForTheExecutionToEnd()
    {
        server.OnPrompt = body => body.TryGetProperty("text", out _) ? server.Reply("ok") : Turn(FakeOpenCodeServer.Step.Event("session.compaction.started", new { reason = "manual" }), Tokens(40, 5));
        await using IPromptSession session = await Create();
        await session.Request("one");

        await session.Compact();
        PromptContext context = await session.GetContext();

        Assert.Single(server.Requests, request => request is { Method: "POST", Path: "/api/session/ses_fake/compact" });
        Assert.Equal((45L, 32000L), (context.Tokens, context.Limit));
    }

    [Fact]
    public async Task Compact_ThatTheToolReportsAsFailedFailsWithItsMessage()
    {
        server.OnPrompt = body => body.TryGetProperty("text", out _) ? server.Reply("ok") : [FakeOpenCodeServer.Step.Event("session.execution.started", new { }), FakeOpenCodeServer.Step.Event("session.compaction.failed", new { reason = "manual", error = new { type = "compaction.failed", message = "Compaction summary did not match the required template" } }), FakeOpenCodeServer.Step.Event("session.execution.succeeded", new { })];
        await using IPromptSession session = await Create();
        await session.Request("one");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Compact());

        Assert.Equal("Compaction summary did not match the required template", exception.Message);
    }

    [Fact]
    public async Task GetContext_OfASessionThatWasJustLoaded_IsReadFromTheLastAnswerOfTheStoredConversationWithTheWindowOfTheServer()
    {
        string export = """{"info":{},"messages":[{"type":"user","text":"hi","time":{"created":1000}},{"type":"assistant","time":{"created":2000},"content":[{"type":"text","text":"hello"}],"tokens":{"input":40,"output":10,"cache":{"read":5,"write":0}}}]}""";
        server.OnPrompt = _ => server.Reply("ok");
        IPromptProvider provider = Provider(Engine(export: export));
        IPromptSession first = await provider.Create(new PromptProviderOptions { WorkspacePath = temp.Path });
        await first.Request("one");
        string id = first.Id;
        await first.DisposeAsync();

        await using IPromptSession loaded = await provider.Load(id);
        PromptContext context = await loaded.GetContext();

        Assert.Equal((55L, 32000L), (context.Tokens, context.Limit));
    }

    [Fact]
    public async Task Fork_WithoutAPoint_CopiesTheWholeConversationAndOpensTheNewSession()
    {
        server.OnPrompt = _ => server.Reply("ok");
        server.KnownSessions = ["ses_forked"];
        await using IPromptSession session = await Create(fork: """{"data":{"id":"ses_forked"}}""", export: """{"info":{},"messages":[]}""");
        await session.Request("one");

        await using IPromptSession fork = await session.Fork();
        server.SessionId = "ses_forked";
        await fork.Request("two");

        Assert.Equal(["api", "--standalone", "POST", "/api/session/ses_fake/fork", "-d", "{}"], await fake.Arguments("api"));
        Assert.Contains(server.Requests, request => request is { Method: "GET", Path: "/api/session/ses_forked" });
    }

    [Fact]
    public async Task Fork_AtAnEntryOfTheHistory_CopiesTheConversationBeforeTheNextInput()
    {
        string export = """
            {"info":{},"messages":[
              {"id":"msg_u1","type":"user","text":"first","time":{"created":1000}},
              {"id":"msg_a1","type":"assistant","time":{"created":2000},"content":[{"type":"text","text":"one"}]},
              {"id":"msg_i1","type":"idle","time":{"created":2500}},
              {"id":"msg_u2","type":"user","text":"second","time":{"created":3000}},
              {"id":"msg_a2","type":"assistant","time":{"created":4000},"content":[{"type":"text","text":"two"}]}]}
            """;
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create(fork: """{"data":{"id":"ses_forked"}}""", export: export);
        await session.Request("go");
        List<PromptHistoryEntry> history = await session.GetHistory().ToListAsync();

        await using IPromptSession early = await session.Fork(history.Single(entry => entry.Event is PromptMessageEvent { Text: "one" }));
        string[] arguments = [.. await fake.Arguments("api")];
        await using IPromptSession late = await session.Fork(history.Single(entry => entry.Event is PromptMessageEvent { Text: "two" }));

        Assert.Equal("""{"before":"msg_u2"}""", arguments[^1]);
        Assert.Equal("{}", (await fake.Arguments("api"))[^1]);
    }

    [Fact]
    public async Task Rewind_ContinuesInTheBranchBeforeTheNextInput()
    {
        string export = """
            {"info":{},"messages":[
              {"id":"msg_u1","type":"user","text":"first","time":{"created":1000}},
              {"id":"msg_a1","type":"assistant","time":{"created":2000},"content":[{"type":"text","text":"one"}]},
              {"id":"msg_u2","type":"user","text":"second","time":{"created":3000}},
              {"id":"msg_a2","type":"assistant","time":{"created":4000},"content":[{"type":"text","text":"two"}]}]}
            """;
        server.OnPrompt = _ => server.Reply("ok");
        server.KnownSessions = ["ses_forked"];
        await using IPromptSession session = await Create(fork: """{"data":{"id":"ses_forked"}}""", export: export);
        await session.Request("go");
        PromptHistoryEntry first = (await session.GetHistory().ToListAsync()).Single(entry => entry.Event is PromptInputEvent { Text: "first" });

        await session.Rewind(first);
        server.SessionId = "ses_forked";
        await session.Request("again");

        Assert.Equal("""{"before":"msg_u2"}""", (await fake.Arguments("api"))[^1]);
        Assert.Contains(server.Requests, request => request is { Method: "GET", Path: "/api/session/ses_forked" });
    }

    [Fact]
    public async Task Fork_AtAPointThatIsNotInTheConversationFails()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create(export: """{"info":{},"messages":[{"id":"msg_u1","type":"user","text":"first","time":{"created":5000}}]}""");
        await session.Request("go");

        await Assert.ThrowsAsync<PromptSessionException>(() => session.Fork(new PromptHistoryPoint(10)));
    }

    [Fact]
    public async Task Fork_WhenTheToolRefusesIt_FailsWithItsMessage()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create(fork: """{"_tag":"SessionNotFoundError","message":"Session not found: ses_fake"}""");
        await session.Request("go");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Fork());

        Assert.Equal("Session not found: ses_fake", exception.Message);
    }

    [Fact]
    public async Task Delete_AsksTheToolToDeleteTheSession()
    {
        server.OnPrompt = _ => server.Reply("ok");
        IPromptProvider provider = Provider(Engine());
        IPromptSession session = await provider.Create(new PromptProviderOptions { WorkspacePath = temp.Path });
        await session.Request("one");
        string id = session.Id;
        await session.DisposeAsync();

        Assert.True(await provider.Delete(id));

        Assert.Equal(["session", "delete", "--standalone", "ses_fake"], await fake.Arguments("session"));
    }

    [Fact]
    public async Task GetHistory_ReadsTheExportOfTheSessionNewestFirst()
    {
        string export = """
            {"info":{},"messages":[
              {"type":"user","text":"list the files","time":{"created":1000}},
              {"type":"assistant","time":{"created":2000},"content":[
                {"type":"text","text":"Looking."},
                {"type":"tool","id":"c1","name":"liveai_find_files","state":{"status":"completed","input":{"pattern":"*.cs"},"content":[{"type":"text","text":"a.cs"}]}},
                {"type":"tool","id":"c2","name":"liveai_read_file","state":{"status":"error","input":{"path":"x"},"error":"missing"}}]},
              {"type":"assistant","time":{"created":3000},"content":[{"type":"text","text":"Done."}]},
              {"type":"idle","time":{"created":4000}}]}
            """;
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create(export: export);
        await session.Request("one");

        List<PromptHistoryEntry> entries = await session.GetHistory().ToListAsync();

        Assert.Equal<PromptEvent>(
        [
            new PromptMessageEvent { Text = "Done." },
            new PromptToolResultEvent { Id = "c2", Output = "missing", IsError = true },
            new PromptToolCallEvent { Id = "c2", Server = "liveai", Name = "read_file", Input = """{"path":"x"}""" },
            new PromptToolResultEvent { Id = "c1", Output = "a.cs", IsError = false },
            new PromptToolCallEvent { Id = "c1", Server = "liveai", Name = "find_files", Input = """{"pattern":"*.cs"}""" },
            new PromptMessageEvent { Text = "Looking." },
            new PromptInputEvent { Text = "list the files" }
        ], entries.Select(entry => entry.Event));
        Assert.True(entries.Zip(entries.Skip(1)).All(pair => pair.First.Point.CompareTo(pair.Second.Point) > 0));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(3000), entries[0].Time);
    }

    [Fact]
    public async Task GetHistory_OfASessionThatIsNotFound_IsEmpty()
    {
        server.OnPrompt = _ => server.Reply("ok");
        await using IPromptSession session = await Create();
        await session.Request("one");

        Assert.Empty(await session.GetHistory().ToListAsync());
    }

    [Fact]
    public async Task Test_RunsTheToolOnTheFirstModelOfTheProvidersItWasGivenAtItsLowestSetting()
    {
        OpenCodeModelProvider provider = new() { Id = "fake", BaseUrl = "http://localhost:9/v1", Models = [new OpenCodeModel { Id = "small" }, new OpenCodeModel { Id = "big" }] };
        IPromptProvider tested = Provider(Engine([provider]));

        PromptTestResult result = await tested.Test("key");

        Assert.True(result.IsAvailable);
        Assert.Equal(["run", "--standalone", "--format", "json"], await fake.Arguments("run"));
        Assert.Contains("\"model\":\"fake/small\"", await File.ReadAllTextAsync(temp.Combine("opencode-config.txt")));
        Assert.Contains("1", await File.ReadAllTextAsync(temp.Combine("opencode-run-prompt.txt")));
    }

    [Fact]
    public async Task Test_OfAToolThatCannotBeStarted_ReportsItUnavailable()
    {
        IPromptProvider provider = Provider(Engine(command: temp.Combine("missing-opencode")));

        PromptTestResult result = await provider.Test();

        Assert.False(result.IsAvailable);
        Assert.Contains("could not be started", result.Error);
    }

    [Fact]
    public async Task TheToolIsStartedWithTheConfigurationOfTheSessionAndTheTokenOnlyInItsEnvironment()
    {
        server.OnPrompt = _ => server.Reply("ok");
        server.OtherMcpServers = ["docs", "files"];
        await using IPromptSession session = await Create();
        session.Configuration.Instructions = "Be brief.";
        session.Transient.McpServers["docs"] = PromptMcpServer.Http("http://localhost:9/mcp", "DOCS_TOKEN");
        session.Transient.McpServers["files"] = PromptMcpServer.Stdio("npx", "-y", "files-server") with { EnvironmentVariables = new Dictionary<string, string> { ["ROOT"] = "/data" } };

        await session.Request("go");

        JsonElement config = await fake.Config();
        Assert.Equal("fake/model", config.GetProperty("model").GetString());
        JsonElement servers = config.GetProperty("mcp").GetProperty("servers");
        Assert.Equal(["liveai", "docs", "files"], servers.EnumerateObject().Select(entry => entry.Name));
        JsonElement library = servers.GetProperty("liveai");
        Assert.Equal(("remote", false, "Bearer {env:LIVEAI_TOOLS_TOKEN}"), (library.GetProperty("type").GetString(), library.GetProperty("codemode").GetBoolean(), library.GetProperty("headers").GetProperty("Authorization").GetString()));
        Assert.Equal((30_000, 30_000, 3_700_000), (library.GetProperty("timeout").GetProperty("startup").GetInt32(), library.GetProperty("timeout").GetProperty("catalog").GetInt32(), library.GetProperty("timeout").GetProperty("execution").GetInt32()));
        Assert.Equal("Bearer {env:DOCS_TOKEN}", servers.GetProperty("docs").GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.False(servers.GetProperty("docs").GetProperty("timeout").TryGetProperty("execution", out _));
        Assert.Equal(["npx", "-y", "files-server"], servers.GetProperty("files").GetProperty("command").EnumerateArray().Select(part => part.GetString()!));
        Assert.Equal("/data", servers.GetProperty("files").GetProperty("environment").GetProperty("ROOT").GetString());
        Assert.Equal([("*", "deny"), ("liveai_*", "allow"), ("docs_*", "allow"), ("files_*", "allow")], config.GetProperty("permissions").EnumerateArray().Select(rule => (rule.GetProperty("action").GetString()!, rule.GetProperty("effect").GetString()!)));
        Assert.Contains("Be brief.", await File.ReadAllTextAsync(config.GetProperty("instructions")[0].GetString()!));
        Assert.DoesNotContain(await File.ReadAllTextAsync(temp.Combine("opencode-token.txt")), await File.ReadAllTextAsync(temp.Combine("opencode-config.txt")));
        Assert.NotEmpty(await File.ReadAllTextAsync(temp.Combine("opencode-token.txt")));
    }

    [Fact]
    public async Task ThePromptWaitsWhileTheToolServerIsStillConnecting()
    {
        server.OnPrompt = _ => server.Reply("ok");
        server.McpStatuses = ["", "pending", "pending", "connected"];
        await using IPromptSession session = await Create();

        await session.Request("go");

        Assert.True(server.Requests.Count(request => request is { Method: "GET", Path: "/api/mcp" }) >= 4);
        Assert.Single(server.Bodies("POST", "/api/session/ses_fake/prompt"));
    }

    [Fact]
    public async Task AToolServerThatFailedToConnectIsConnectedAgainBeforeThePrompt()
    {
        server.OnPrompt = _ => server.Reply("ok");
        server.McpStatuses = ["failed"];
        await using IPromptSession session = await Create();

        await session.Request("go");

        Assert.Single(server.Requests, request => request is { Method: "POST", Path: "/api/experimental/mcp/liveai/connect" });
    }

    [Fact]
    public async Task AToolServerThatCannotBeConnectedFailsTheRequestBecauseTheAiWouldHaveNoTools()
    {
        server.OnPrompt = _ => server.Reply("ok");
        server.McpStatuses = ["failed"];
        server.ConnectRecovers = false;
        await using IPromptSession session = await Create();

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        Assert.Contains("could not connect to the tools", exception.Message);
        Assert.Empty(server.Bodies("POST", "/api/session/ses_fake/prompt"));
    }

    [Fact]
    public async Task AnExternalServerThatNeverAppearsDoesNotHoldBackThePromptOnceTheLibrarysServerIsConnected()
    {
        server.OnPrompt = _ => server.Reply("ok");
        OpenCodeEngine engine = new(fake.Script(server), []) { ConnectWait = TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromMilliseconds(20) };
        await using IPromptSession session = await Provider(engine).Create(new PromptProviderOptions { WorkspacePath = temp.Path });
        session.Transient.McpServers["docs"] = PromptMcpServer.Http("http://localhost:9/mcp");

        Assert.Equal("ok", await session.Request("go"));
    }

    [Fact]
    public async Task AToolServerThatNeverFinishesConnectingFailsTheRequestInTime()
    {
        server.McpStatuses = ["pending"];
        OpenCodeEngine engine = new(fake.Script(server), []) { ConnectWait = TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromMilliseconds(20) };
        await using IPromptSession session = await Provider(engine).Create(new PromptProviderOptions { WorkspacePath = temp.Path });

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        Assert.Contains("did not connect to its tools in time", exception.Message);
    }

    [Fact]
    public async Task WhenTheSessionIsDisposedTheServerEndsBecauseItsInputWasClosed()
    {
        server.OnPrompt = _ => server.Reply("ok");
        IPromptSession session = await Create();
        await session.Request("go");
        int pid = int.Parse((await File.ReadAllLinesAsync(temp.Combine("pids.txt")))[0]);

        await session.DisposeAsync();

        bool gone = false;

        for (int attempt = 0; attempt < 100 && !gone; attempt++)
        {
            try
            {
                using Process alive = Process.GetProcessById(pid);
                gone = alive.HasExited;
            }
            catch (ArgumentException)
            {
                gone = true;
            }

            await Task.Delay(50);
        }

        Assert.True(gone);
    }

    private sealed record Answer(string Summary, int Score);
}
