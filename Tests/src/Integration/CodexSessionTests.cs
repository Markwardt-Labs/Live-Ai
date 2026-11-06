namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class CodexSessionTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly FakeCodex fake;

    public CodexSessionTests()
        => fake = new FakeCodex(temp);

    public void Dispose()
        => temp.Dispose();

    private IPromptProvider Provider(IPromptEngine engine)
        => TestProviders.Make(engine, temp.Combine("data"));

    private IPromptEngine Codex(string command)
        => new CodexEngine(command, temp.Combine("codex-state"));

    private async Task<IPromptSession> Create(string command, bool persistent = false)
    {
        IPromptSession session = temp.Track(await Provider(Codex(command)).Create(TestModels.Options));
        return session;
    }

    private async Task<int[]> Pids()
        => (await File.ReadAllLinesAsync(temp.Combine("pids.txt"))).Select(int.Parse).ToArray();

    private string Emit(string json)
        => $"printf '%s\\n' '{json.Replace("'", "'\\''")}' | sed \"s/@TID@/$tid/g; s/@TURN@/$turn/g\"";

    private string Completed(string status = "completed", string error = "")
        => Emit($$$$"""{"method":"turn/completed","params":{"threadId":"@TID@","turn":{"id":"@TURN@","status":"{{{{status}}}}"{{{{error}}}}}}}""");

    [Fact]
    public async Task Send_WhileTheTurnRuns_SteersTheInputIntoTheRunningTurn()
    {
        await using IPromptSession session = await Create(fake.Script(turn: ":", steer: Completed()));

        await session.Send("first");
        await WaitFor(() => session.State == PromptSessionState.Working && File.Exists(temp.Combine("turn-started")));
        await Task.Delay(300);
        await session.Send("second");
        await WaitFor(() => session.State == PromptSessionState.Waiting);

        JsonElement steer = Assert.Single(await fake.Parameters("turn/steer"));
        Assert.Equal("turn-1", steer.GetProperty("expectedTurnId").GetString());
        Assert.Contains("second", steer.GetProperty("input").ToString());
        Assert.Single(await fake.Parameters("turn/start"));
        Assert.Equal(PromptSessionState.Waiting, session.State);
    }

    private async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task AReply_ComesAsDeltasThenTheMessageThenTheUsageOfTheTurn()
    {
        await using IPromptSession session = await Create(fake.Script(reply: "hello"));

        List<PromptEvent> events = await session.EventList("hi");

        Assert.Equal<PromptEvent>([new PromptDeltaEvent { Text = "hello" }, new PromptMessageEvent { Text = "hello" }, new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 6, CachedInputTokens = 4, OutputTokens = 5 } }], events);
    }

    [Fact]
    public async Task TheThreadIsStartedWithTheGuidanceAndTheInstructionsAndTheTurnCarriesTheModelAndTheEffort()
    {
        await using IPromptSession session = await Create(fake.Script());
        session.Configuration.Instructions = "Be brief.";
        session.Configuration.Effort = PromptEffort.High;

        await session.Request("hi");

        JsonElement thread = Assert.Single(await fake.Parameters("thread/start"));
        Assert.Contains("Instructions from the user:\nBe brief.", thread.GetProperty("developerInstructions").GetString());
        Assert.Equal(("never", "read-only", false), (thread.GetProperty("approvalPolicy").GetString(), thread.GetProperty("sandbox").GetString(), thread.GetProperty("ephemeral").GetBoolean()));
        Assert.Equal(PromptDirectory(session), thread.GetProperty("cwd").GetString());
        JsonElement turn = Assert.Single(await fake.Parameters("turn/start"));
        Assert.Equal(("gpt-5.6-terra", "high", "codex-thread"), (turn.GetProperty("model").GetString(), turn.GetProperty("effort").GetString(), turn.GetProperty("threadId").GetString()));
        Assert.Equal(("text", "hi"), (turn.GetProperty("input")[0].GetProperty("type").GetString(), turn.GetProperty("input")[0].GetProperty("text").GetString()));
    }

    private string PromptDirectory(IPromptSession session)
        => session.WorkspacePath;

    [Fact]
    public async Task AfterAMove_TheNextPromptResumesTheThreadInTheNewFolder()
    {
        string moved = Directory.CreateDirectory(temp.Combine("moved")).FullName;
        await using IPromptSession session = await Create(fake.Script());
        await session.Request("one");

        await session.Move(moved);
        await session.Request("two");

        JsonElement resume = Assert.Single(await fake.Parameters("thread/resume"));
        Assert.Equal(("codex-thread", moved), (resume.GetProperty("threadId").GetString(), resume.GetProperty("cwd").GetString()));
        Assert.Equal(2, (await Pids()).Length);
    }

    [Fact]
    public async Task AToolCallOfTheAi_IsReportedWhenItStartsAndWhenItEnds()
    {
        string turn = string.Join('\n',
            Emit("""{"method":"item/started","params":{"turnId":"@TURN@","item":{"type":"mcpToolCall","id":"c1","server":"liveai","tool":"read_file","arguments":{"path":"a.txt"},"status":"inProgress"}}}"""),
            Emit("""{"method":"item/completed","params":{"turnId":"@TURN@","item":{"type":"mcpToolCall","id":"c1","server":"liveai","tool":"read_file","arguments":{"path":"a.txt"},"status":"completed","result":{"content":[{"type":"text","text":"file text"}]}}}}"""),
            Emit("""{"method":"item/completed","params":{"turnId":"@TURN@","item":{"type":"mcpToolCall","id":"c2","server":"liveai","tool":"x","arguments":null,"status":"failed","error":{"message":"it broke"}}}}"""),
            Completed());
        await using IPromptSession session = await Create(fake.Script(turn: turn));

        List<PromptEvent> events = await session.EventList("go");

        Assert.Equal<PromptEvent>(
        [
            new PromptToolCallEvent { Id = "c1", Server = "liveai", Name = "read_file", Input = """{"path":"a.txt"}""" },
            new PromptToolResultEvent { Id = "c1", Output = "file text", IsError = false },
            new PromptToolResultEvent { Id = "c2", Output = "it broke", IsError = true },
            new PromptUsageEvent { Usage = new PromptUsage() }
        ], events);
    }

    [Fact]
    public async Task ASearchOfTheCodexItselfIsReportedAsAToolCallAndItsResult()
    {
        string turn = string.Join('\n',
            Emit("""{"method":"item/started","params":{"turnId":"@TURN@","item":{"type":"webSearch","id":"w1","query":"dotnet release"}}}"""),
            Emit("""{"method":"item/completed","params":{"turnId":"@TURN@","item":{"type":"webSearch","id":"w1","query":"dotnet release"}}}"""),
            Completed());
        await using IPromptSession session = await Create(fake.Script(turn: turn));

        List<PromptEvent> events = await session.EventList("go");

        Assert.Equal<PromptEvent>(
        [
            new PromptToolCallEvent { Id = "w1", Server = null, Name = "web_search", Input = """{"query":"dotnet release"}""" },
            new PromptToolResultEvent { Id = "w1", Output = "dotnet release", IsError = false },
            new PromptUsageEvent { Usage = new PromptUsage() }
        ], events);
    }

    [Fact]
    public async Task TheCodexIsAllowedItsOwnSearchOnlyWhenTheSessionAllowsTheWeb()
    {
        await using IPromptSession without = await Create(fake.Script());
        await without.Request("hi");
        await using IPromptSession with = await Create(fake.Script());
        with.Configuration.IsWebAllowed = true;
        await with.Request("hi");

        IReadOnlyList<JsonElement> threads = await fake.Parameters("thread/start");

        Assert.False(threads[0].TryGetProperty("config", out _));
        Assert.Equal("live", threads[1].GetProperty("config").GetProperty("web_search").GetString());
    }

    [Fact]
    public async Task ThingsOfAnotherTurnOrThatAreNotTheAgentsAreLeftOut()
    {
        string turn = string.Join('\n',
            Emit("""{"method":"item/completed","params":{"turnId":"another","item":{"type":"agentMessage","id":"x","text":"not mine"}}}"""),
            Emit("""{"method":"item/completed","params":{"turnId":"@TURN@","item":{"type":"reasoning","id":"r","text":"thinking"}}}"""),
            Emit("""{"method":"item/completed","params":{"turnId":"@TURN@","item":{"type":"agentMessage","id":"blank","text":"  "}}}"""),
            Emit("""{"method":"thread/tokenUsage/updated","params":{"turnId":"another","tokenUsage":{"last":{"inputTokens":999}}}}"""),
            "echo 'not json at all'",
            Emit("""{"method":"item/completed","params":{"turnId":"@TURN@","item":{"type":"agentMessage","id":"m","text":"mine"}}}"""),
            Completed());
        await using IPromptSession session = await Create(fake.Script(turn: turn));

        Assert.Equal("mine", await session.Request("go"));
    }

    [Fact]
    public async Task ATurnThatFailed_FailsThePromptWithItsMessage()
    {
        await using IPromptSession session = await Create(fake.Script(turn: Completed("failed", ""","error":{"message":"{\"error\":{\"message\":\"quota used up\"}}"}""")));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        Assert.Equal("quota used up", exception.Message);
    }

    [Fact]
    public async Task AnInterruptedTurn_EndsTheStreamNormally()
    {
        await using IPromptSession session = await Create(fake.Script(turn: Completed("interrupted")));

        List<PromptEvent> events = await session.EventList("go");

        Assert.IsType<PromptUsageEvent>(Assert.Single(events));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupt_StopsTheTurnThroughTheAppServerAndTheSessionGoesOn(bool persistent)
    {
        string turn = "( while [ ! -f \"$PWD/interrupted\" ]; do sleep 0.05; done; " + Completed("interrupted") + " ) &";
        await using IPromptSession session = await Create(fake.Script(turn: turn), persistent);
        Task<List<PromptEvent>> running = session.EventList("long");
        await WaitFor(async () => (await fake.Parameters("turn/start")).Count == 1);

        Assert.True(await session.Interrupt());
        List<PromptEvent> events = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(events, output => output is PromptInterruptedEvent);
        JsonElement interrupt = Assert.Single(await fake.Parameters("turn/interrupt"));
        Assert.Equal(("codex-thread", "turn-1"), (interrupt.GetProperty("threadId").GetString(), interrupt.GetProperty("turnId").GetString()));
        File.Delete(temp.Combine("interrupted"));
    }

    [Fact]
    public async Task Interrupt_BeforeTheAppServerHasAnsweredTheStartOfTheTurn_IsSentAsSoonAsTheTurnIsKnown()
    {
        File.WriteAllText(temp.Combine("slow-start"), string.Empty);
        string turn = "( while [ ! -f \"$PWD/interrupted\" ]; do sleep 0.05; done; " + Completed("interrupted") + " ) &";
        await using IPromptSession session = await Create(fake.Script(turn: turn));
        Task<List<PromptEvent>> running = session.EventList("long");
        await WaitFor(async () => (await fake.Parameters("turn/start")).Count == 1);

        Assert.True(await session.Interrupt());
        List<PromptEvent> events = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(events, output => output is PromptInterruptedEvent);
        Assert.Equal("turn-1", Assert.Single(await fake.Parameters("turn/interrupt")).GetProperty("turnId").GetString());
        File.Delete(temp.Combine("interrupted"));
    }

    private async Task WaitFor(Func<Task<bool>> condition)
    {
        for (int attempt = 0; attempt < 200 && !await condition(); attempt++)
        {
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task ANote_IsAddedAsADeveloperMessageBeforeTheTurn()
    {
        await using IPromptSession session = await Create(fake.Script());

        await session.Send("go", new PromptSendOptions { Notes = ["the build passes"] });
        await WaitFor(() => session.State == PromptSessionState.Waiting && File.Exists(temp.Combine("turn-count")));

        string[] methods = [.. (await fake.Requests()).Select(request => request.TryGetProperty("method", out JsonElement method) ? method.GetString()! : string.Empty)];
        Assert.True(Array.IndexOf(methods, "thread/inject_items") < Array.IndexOf(methods, "turn/start"));
        JsonElement item = Assert.Single(await fake.Parameters("thread/inject_items")).GetProperty("items")[0];
        Assert.Equal(("message", "developer", "the build passes"), (item.GetProperty("type").GetString(), item.GetProperty("role").GetString(), item.GetProperty("content")[0].GetProperty("text").GetString()));
    }

    [Fact]
    public async Task ChangedInstructions_AreToldToTheThreadBecauseCodexKeepsTheOnesItBeganWith()
    {
        await using IPromptSession session = await Create(fake.Script());
        session.Configuration.Instructions = "Be brief.";
        await session.Request("one");
        session.Configuration.Instructions = "Answer in French.";

        await session.Request("two");
        session.Configuration.Instructions = null;
        await session.Request("three");

        IReadOnlyList<JsonElement> injected = await fake.Parameters("thread/inject_items");
        Assert.Equal(2, injected.Count);
        Assert.Contains("Answer in French.", injected[0].GetProperty("items")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Contains("no longer apply", injected[1].GetProperty("items")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Compact_AsksTheThreadToCompactAndWaitsForTheCompactionTurn()
    {
        await using IPromptSession session = await Create(fake.Script());
        await session.Request("one");

        await session.Compact();

        Assert.Equal("codex-thread", Assert.Single(await fake.Parameters("thread/compact/start")).GetProperty("threadId").GetString());
        Assert.Equal(PromptContextOf(40, 1000), await session.GetContext());
    }

    private PromptContext PromptContextOf(long tokens, long limit)
        => new() { Tokens = tokens, Limit = limit };

    [Fact]
    public async Task GetContext_OfAResumedThread_IsWhatTheAppServerReportsOnResume()
    {
        IPromptProvider provider = Provider(Codex(fake.Script()));
        IPromptSession first = temp.Track(await provider.Create(TestModels.Options));
        await first.Request("one");

        IPromptSession loaded = await provider.Load(first.Id);

        Assert.Equal(PromptContextOf(40, 1000), await loaded.GetContext());
    }

    [Fact]
    public async Task GetContext_OfAThreadThatHasNoTurnYet_ReportsNothingOnceTheWaitIsOver()
    {
        CodexEngine provider = new(fake.Script(), temp.Path) { ContextWait = TimeSpan.FromMilliseconds(100) };
        PromptSessionRecord record = new() { Id = Guid.NewGuid(), Engine = provider, WorkingFolder = temp.Path };
        PromptEngineStart given = record.ToStart(null, null, null);
        await using IPromptProcess process = new TurnRunner().Open(provider, provider.CreatePersistentStart(given), given);

        PromptContext context = await process.GetContext();

        Assert.Equal(new PromptContext { Tokens = 0, Limit = 0 }, context);
        Assert.Equal(0, context.Used);
    }

    [Fact]
    public async Task RequestsOfTheAppServer_AreAnsweredWithAnErrorSoItNeverWaitsForUs()
    {
        string turn = string.Join('\n', Emit("""{"id":77,"method":"item/tool/requestUserInput","params":{"threadId":"@TID@"}}"""), "sleep 0.3", Emit("""{"method":"item/completed","params":{"turnId":"@TURN@","item":{"type":"agentMessage","id":"m","text":"done"}}}"""), Completed());
        await using IPromptSession session = await Create(fake.Script(turn: turn));

        Assert.Equal("done", await session.Request("go"));

        JsonElement answer = default;

        for (int attempt = 0; attempt < 100 && answer.ValueKind == JsonValueKind.Undefined; attempt++)
        {
            answer = (await fake.Requests()).FirstOrDefault(request => request.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 77);

            if (answer.ValueKind == JsonValueKind.Undefined)
            {
                await Task.Delay(50);
            }
        }

        Assert.NotEqual(JsonValueKind.Undefined, answer.ValueKind);
        Assert.False(answer.TryGetProperty("method", out JsonElement _));
        Assert.True(answer.GetProperty("error").TryGetProperty("message", out JsonElement _));
    }

    [Fact]
    public async Task AnAppServerThatStopsInTheMiddleOfATurn_FailsThePrompt()
    {
        await using IPromptSession session = await Create(fake.Script(turn: "exit 3"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("go"));

        Assert.Contains("stopped", exception.Message);
    }

    [Fact]
    public async Task AnAppServerThatRefusesTheThreadOrTheTurn_FailsThePromptWithItsReason()
    {
        string refuseThread = temp.Script("refuse-thread", "while IFS= read -r line; do case \"$line\" in *'\"method\":\"initialize\"'*) echo '{\"id\":1,\"result\":{}}';; *thread/start*) echo '{\"id\":2,\"error\":{\"message\":\"not signed in\"}}';; esac; done");
        string refuseTurn = temp.Script("refuse-turn", "while IFS= read -r line; do case \"$line\" in *'\"method\":\"initialize\"'*) echo '{\"id\":1,\"result\":{}}';; *thread/start*) echo '{\"id\":2,\"result\":{\"thread\":{\"id\":\"t\"}}}';; *turn/start*) echo '{\"id\":3,\"error\":{\"message\":\"unknown model\"}}';; esac; done");

        PromptSessionException thread = await Assert.ThrowsAsync<PromptSessionException>(async () => await (await Create(refuseThread)).Request("go"));
        PromptSessionException turn = await Assert.ThrowsAsync<PromptSessionException>(async () => await (await Create(refuseTurn)).Request("go"));

        Assert.Equal("not signed in", thread.Message);
        Assert.Equal("unknown model", turn.Message);
    }

    [Fact]
    public async Task AnAppServerThatAnswersWithoutAThread_FailsThePrompt()
    {
        string script = temp.Script("no-thread", "while IFS= read -r line; do case \"$line\" in *'\"method\":\"initialize\"'*) echo '{\"id\":1,\"result\":{}}';; *thread/start*) echo '{\"id\":2,\"result\":{}}';; esac; done");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(async () => await (await Create(script)).Request("go"));

        Assert.Contains("did not report the thread", exception.Message);
    }

    [Fact]
    public async Task ACommandThatCannotBeStarted_FailsWithTheReason()
    {
        await using IPromptSession session = await Create(temp.Combine("missing"));

        await Assert.ThrowsAsync<PromptToolStartException>(() => session.Request("go"));
    }

    [Fact]
    public async Task ACancelledPrompt_EndsTheAppServerAndTheSessionStartsAnother()
    {
        await using IPromptSession session = await Create(fake.Script(turn: "sleep 30"), true);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Request("go", null, cancellation: cancellation.Token));

        int first = (await Pids())[0];
        await WaitFor(() => Task.FromResult(!Alive(first)));
        Assert.False(Alive(first));
    }

    private bool Alive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
