namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class PersistentSessionTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptToolService Service(IPromptEngine engine)
        => TestServices.Make(engine, temp.Combine("data"));

    private string Reply(string text, string session = "persistent-session")
        => $$$"""
        echo "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"{{{text}}}\"}]}}"
        echo '{"type":"result","is_error":false,"session_id":"{{{session}}}"}'
        """;

    private string Controls
        => """
        case "$line" in
        *'"subtype":"set_model"'*|*'"subtype":"apply_flag_settings"'*|*'"subtype":"get_context_usage"'*)
        rid=$(printf '%s' "$line" | sed -n 's/.*"request_id":"\([^"]*\)".*/\1/p')
        echo "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"$rid\",\"response\":{\"totalTokens\":123,\"maxTokens\":1000}}}"
        continue
        ;;
        esac
        """;

    private string Structured(string json)
        => $$$"""
        echo '{"type":"result","is_error":false,"session_id":"persistent-session","structured_output":{{{json}}}}'
        """;

    private async Task<IPromptSession> Session(string body, string? prefix = null, string? controls = null)
    {
        IPromptEngine engine = new ClaudeEngine(temp.Script("claude", $"printf '%s\\n' \"$@\" > \"$PWD/args.txt\"\necho $$ >> \"$PWD/pids.txt\"\n{prefix}\nn=0\nwhile IFS= read -r line; do\nn=$((n+1))\nprintf '%s\\n' \"$line\" >> \"$PWD/frames.txt\"\n{controls ?? Controls}\n{body}\ndone"), temp.Combine("claude-state"));

        IPromptSession session = temp.Track(await Service(engine).Create(TestModels.Options));
        return session;
    }

    [Fact]
    public async Task Send_WhileTheAiIsWorking_ReachesTheRunningProcessAtOnceAndIsAnsweredWithTheSameWork()
    {
        IPromptSession session = await Session("""
        echo '{"type":"user","isReplay":true,"message":{"role":"user","content":"x"}}'
        if [ "$n" = 1 ]; then continue; fi
        echo '{"type":"assistant","message":{"content":[{"type":"text","text":"took both"}]}}'
        echo '{"type":"result","is_error":false,"session_id":"persistent-session"}'
        """);
        Recorder<PromptEvent> recorder = new();
        session.Subscribe(recorder);

        await session.Send("first");
        await WaitFor(() => session.State == PromptSessionState.Working && File.Exists(temp.Combine("frames.txt")));
        await session.Send("second");
        await WaitFor(() => session.State == PromptSessionState.Waiting && recorder.Values.OfType<PromptMessageEvent>().Any());

        string[] frames = await File.ReadAllLinesAsync(temp.Combine("frames.txt"));
        Assert.Equal(2, frames.Length);
        Assert.Contains("first", frames[0]);
        Assert.Contains("second", frames[1]);
        Assert.Equal(["took both"], recorder.Values.OfType<PromptMessageEvent>().Select(message => message.Text));
        Assert.Equal([PromptSessionState.Working, PromptSessionState.Waiting], recorder.Values.OfType<PromptStateEvent>().Select(change => change.State));
        Assert.Single(await Pids());
    }

    [Fact]
    public async Task Send_WhenTheWorkFailsBeforeTheAiTookTheSteeredInput_EndsTheProcessSoItsLateOutputCannotReachTheNextPrompt()
    {
        IPromptSession session = await Session("""
        echo '{"type":"user","isReplay":true,"message":{"role":"user","content":"x"}}'
        if [ "$n" = 1 ]; then
        read -r second
        echo '{"type":"result","is_error":true,"subtype":"error_during_execution","session_id":"persistent-session","result":"boom"}'
        fi
        """);
        await session.Send("first");
        await WaitFor(() => session.State == PromptSessionState.Working && File.Exists(temp.Combine("pids.txt")));
        int pid = (await Pids())[0];

        await session.Send("second");
        await WaitFor(() => session.State == PromptSessionState.Waiting);

        await WaitFor(() => !Alive(pid));
        Assert.False(Alive(pid));
    }

    private async Task<int[]> Pids()
        => (await File.ReadAllLinesAsync(temp.Combine("pids.txt"))).Select(int.Parse).ToArray();

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

    private async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }
    }

    private string Interruptible()
        => """
        case "$line" in
        *control_request*)
        kill $bg 2>/dev/null
        echo '{"type":"assistant","message":{"content":[{"type":"text","text":"partial"}]}}'
        echo '{"type":"result","is_error":true,"subtype":"error_during_execution","session_id":"persistent-session"}'
        ;;
        *slow*)
        sleep 30 &
        bg=$!
        ;;
        *)
        echo '{"type":"assistant","message":{"content":[{"type":"text","text":"fine"}]}}'
        echo '{"type":"result","is_error":false,"session_id":"persistent-session"}'
        ;;
        esac
        """;

    [Fact]
    public async Task Interrupt_StopsThePromptInTheSameProcessWhichThenTakesTheNextOne()
    {
        await using IPromptSession session = await Session(Interruptible());
        Task<List<PromptEvent>> slow = session.EventList("slow task");
        await Task.Delay(250);

        Assert.True(await session.Interrupt());
        List<PromptEvent> events = await slow.WaitAsync(TimeSpan.FromSeconds(10));
        string next = await session.Request("quick");

        Assert.Equal<PromptEvent>([new PromptMessageEvent { Text = "partial" }, new PromptInterruptedEvent()], events);
        Assert.Equal("fine", next);
        Assert.Single(await Pids());
        Assert.Contains("control_request", await File.ReadAllTextAsync(temp.Combine("frames.txt")));
    }

    [Fact]
    public async Task Events_ReportTheCostOfEachPromptAndNotTheRunningTotalOfTheProcess()
    {
        await using IPromptSession session = await Session("echo '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"persistent-session\",\"total_cost_usd\":'$((n*n)).5',\"usage\":{\"input_tokens\":1,\"output_tokens\":2}}'");

        List<decimal?> costs = [];

        for (int prompt = 0; prompt < 3; prompt++)
        {
            costs.AddRange((await session.Result("go")).Events.OfType<PromptUsageEvent>().Select(usage => usage.Usage.CostUsd));
        }

        Assert.Equal([1.5m, 3.0m, 5.0m], costs);
    }

    [Fact]
    public async Task Ask_KeepsOneProcessForEveryPrompt()
    {
        await using IPromptSession session = await Session(Reply("reply $n"));

        Assert.Equal("reply 1", await session.Request("first"));
        int[] afterFirst = await Pids();
        Assert.True(Alive(afterFirst[0]));
        Assert.Equal("reply 2", await session.Request("second \"quoted\""));

        Assert.Equal(afterFirst, await Pids());
        string[] frames = await File.ReadAllLinesAsync(temp.Combine("frames.txt"));
        Assert.Equal(2, frames.Length);
        Assert.Equal("second \"quoted\"", JsonDocument.Parse(frames[1]).RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.Contains("--input-format", await File.ReadAllLinesAsync(temp.Combine("args.txt")));
    }

    [Fact]
    public async Task DisposeAsync_EndsTheProcess()
    {
        IPromptSession session = await Session(Reply("hi"));
        await session.Request("first");
        int pid = (await Pids())[0];

        await session.DisposeAsync();

        await WaitFor(() => !Alive(pid));
        Assert.False(Alive(pid));
    }

    [Fact]
    public async Task Ask_WhenTheProcessEndedBetweenPrompts_StartsANewOneThatResumesTheSession()
    {
        await using IPromptSession session = await Session($"{Reply("hi")}\nexit 0");

        await session.Request("first");
        int first = (await Pids())[0];
        await WaitFor(() => !Alive(first));
        await session.Request("second");

        int[] pids = await Pids();
        Assert.Equal(2, pids.Length);
        Assert.NotEqual(pids[0], pids[1]);
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Equal("persistent-session", arguments[Array.IndexOf(arguments, "--resume") + 1]);
    }

    [Fact]
    public async Task Ask_WhenThePromptFails_KeepsTheProcess()
    {
        await using IPromptSession session = await Session("if [ $n = 1 ]; then echo '{\"type\":\"result\",\"is_error\":true,\"result\":\"overloaded\"}'; else " + Reply("fine") + "; fi");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("first"));
        string reply = await session.Request("second");

        Assert.Equal("overloaded", exception.Message);
        Assert.Equal("fine", reply);
        Assert.Single(await Pids());
    }

    [Fact]
    public async Task Ask_WhenTheProcessDiesDuringAPrompt_FailsWithItsErrorOutput()
    {
        await using IPromptSession session = await Session("echo 'crashed hard' >&2\nexit 3");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("first"));

        Assert.Equal("crashed hard", exception.Message);
    }

    [Fact]
    public async Task Ask_WhenCancelled_EndsTheProcessAndTheNextPromptStartsANewOne()
    {
        await using IPromptSession session = await Session("if [ ! -f \"$PWD/slept\" ]; then touch \"$PWD/slept\"; sleep 300; else " + Reply("again") + "; fi");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Request("slow", null, cancellation: cancellation.Token));
        int first = (await Pids())[0];
        await WaitFor(() => !Alive(first));

        Assert.False(Alive(first));
        Assert.Equal("again", await session.Request("second"));
        Assert.Equal(2, (await Pids()).Length);
    }

    [Fact]
    public async Task Ask_WhenAChangedSettingNeedsNewFlags_StartsANewProcess()
    {
        await using IPromptSession session = await Session(Reply("hi"));
        await session.Request("first");
        int first = (await Pids())[0];

        session.Configuration.Instructions = "Be brief.";
        await session.Request("second");

        int[] pids = await Pids();
        Assert.Equal(2, pids.Length);
        await WaitFor(() => !Alive(first));
        Assert.False(Alive(first));
        Assert.Contains("Be brief.", string.Join('\n', await File.ReadAllLinesAsync(temp.Combine("args.txt"))));
    }

    [Fact]
    public async Task Ask_WhenTheModelOrTheEffortChanged_TellsTheRunningProcessAndKeepsIt()
    {
        await using IPromptSession session = await Session(Reply("hi"));
        await session.Request("first");

        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Effort = PromptEffort.Low;
        await session.Request("second");

        Assert.Single(await Pids());
        string[] frames = await File.ReadAllLinesAsync(temp.Combine("frames.txt"));
        Assert.Equal(["user", "control_request", "control_request", "user"], frames.Select(frame => JsonDocument.Parse(frame).RootElement.GetProperty("type").GetString()));
        JsonElement model = JsonDocument.Parse(frames[1]).RootElement.GetProperty("request");
        Assert.Equal(("set_model", "opus"), (model.GetProperty("subtype").GetString(), model.GetProperty("model").GetString()));
        JsonElement effort = JsonDocument.Parse(frames[2]).RootElement.GetProperty("request");
        Assert.Equal(("apply_flag_settings", "low"), (effort.GetProperty("subtype").GetString(), effort.GetProperty("settings").GetProperty("effortLevel").GetString()));
    }

    [Fact]
    public async Task GetContext_AsksTheProcessHowMuchOfTheWindowTheConversationFills()
    {
        await using IPromptSession session = await Session(Reply("hi"));
        await session.Request("first");

        PromptContext context = await session.GetContext();

        Assert.Equal((123, 1000, 0.123), (context.Tokens, context.Limit, context.Used));
        Assert.Single(await Pids());
    }

    [Fact]
    public async Task Compact_SendsTheSlashCommandAndFollowsTheSessionTheCompactedConversationContinuesIn()
    {
        string numbered = """
            echo '{"type":"assistant","message":{"content":[{"type":"text","text":"ok"}]}}'
            echo "{\"type\":\"result\",\"is_error\":false,\"session_id\":\"session-$n\"}"
            """;
        await using IPromptSession session = await Session(numbered);
        await session.Request("first");
        string stored = Directory.GetFiles(temp.Combine("data/Sessions")).Single();
        Assert.Contains("session-1", await File.ReadAllTextAsync(stored));

        await session.Compact();

        string[] frames = await File.ReadAllLinesAsync(temp.Combine("frames.txt"));
        Assert.Equal("/compact", JsonDocument.Parse(frames[1]).RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.Contains("session-2", await File.ReadAllTextAsync(stored));
        await session.Request("second");
        Assert.Single(await Pids());
        Assert.Contains("session-3", await File.ReadAllTextAsync(stored));
    }

    private string Answering(string response)
        => $$"""
        case "$line" in
        *'"subtype":"set_model"'*|*'"subtype":"get_context_usage"'*)
        rid=$(printf '%s' "$line" | sed -n 's/.*"request_id":"\([^"]*\)".*/\1/p')
        printf '%s\n' '{"type":"control_response","response":{{response}}}' | sed "s/@RID@/$rid/"
        continue
        ;;
        esac
        """;

    [Fact]
    public async Task ARequestTheToolRefuses_FailsThePromptWithItsReason()
    {
        string refusing = Answering("""{"subtype":"error","request_id":"@RID@","error":"no such model"}""");
        await using IPromptSession session = await Session(Reply("hi"), controls: refusing);
        await session.Request("first");

        session.Configuration.Capability = PromptCapability.Strong;
        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request("second"));

        Assert.Equal("no such model", exception.Message);
    }

    [Fact]
    public async Task GetContext_WhenTheToolDoesNotSayHowMuchIsUsed_Fails()
    {
        string vague = Answering("""{"subtype":"success","request_id":"@RID@","response":{}}""");
        await using IPromptSession session = await Session(Reply("hi"), controls: vague);

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.GetContext());

        Assert.Contains("did not report", exception.Message);
    }

    [Fact]
    public async Task GetContext_WhenTheToolReportsNoWindow_IsASizeOfNothing()
    {
        string windowless = Answering("""{"subtype":"success","request_id":"@RID@","response":{"totalTokens":77}}""");
        await using IPromptSession session = await Session(Reply("hi"), controls: windowless);

        PromptContext context = await session.GetContext();

        Assert.Equal((77, 0, 0), (context.Tokens, context.Limit, context.Used));
    }

    [Fact]
    public async Task AToolThatStopsWhileTheSessionAsksIt_FailsWithoutHanging()
    {
        string stopping = """
            case "$line" in
            *'"subtype":"get_context_usage"'*)
            exit 0
            ;;
            esac
            """;
        await using IPromptSession session = await Session(Reply("hi"), controls: stopping);

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.GetContext());

        Assert.Contains("stopped", exception.Message);
    }

    [Fact]
    public async Task SendWithANote_PutsTheNoteInFrontOfTheInputAsASystemReminder()
    {
        await using IPromptSession session = await Session(Reply("hi"));

        await session.Send("go", new PromptSendOptions { Notes = ["the build passes"] });
        await WaitFor(() => session.State == PromptSessionState.Waiting && File.Exists(temp.Combine("frames.txt")));

        JsonElement content = JsonDocument.Parse((await File.ReadAllLinesAsync(temp.Combine("frames.txt")))[0]).RootElement.GetProperty("message").GetProperty("content");
        Assert.Contains("the build passes", content[0].GetProperty("text").GetString());
        Assert.Equal("go", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Ask_WithCodex_KeepsOneAppServerForEveryPrompt()
    {
        IPromptEngine engine = new CodexEngine(new FakeCodex(temp).Script(reply: "hi"), temp.Combine("codex-state"));
        await using IPromptSession session = temp.Track(await Service(engine).Create(TestModels.Options));

        await session.Request("first");
        await session.Request("second");

        int[] pids = await Pids();
        Assert.Single(pids);
        Assert.True(Alive(pids[0]));
    }

    [Fact]
    public async Task Ask_WithCodex_TakesAChangeOfModelAndEffortWithTheNextPromptWithoutAnotherProcess()
    {
        FakeCodex fake = new(temp);
        IPromptEngine engine = new CodexEngine(fake.Script(reply: "hi"), temp.Combine("codex-state"));
        await using IPromptSession session = temp.Track(await Service(engine).Create(TestModels.Options));
        await session.Request("first");

        session.Configuration.Capability = PromptCapability.Strong;
        session.Configuration.Effort = PromptEffort.Low;
        await session.Request("second");

        Assert.Single(await Pids());
        IReadOnlyList<JsonElement> turns = await fake.Parameters("turn/start");
        Assert.Equal(("gpt-5.6-terra", "high"), (turns[0].GetProperty("model").GetString(), turns[0].GetProperty("effort").GetString()));
        Assert.Equal(("gpt-5.6-sol", "low"), (turns[1].GetProperty("model").GetString(), turns[1].GetProperty("effort").GetString()));
    }

    [Fact]
    public async Task Ask_WithAnOutputType_KeepsTheProcessWhileTheTypeStaysTheSame()
    {
        await using IPromptSession session = await Session(Structured("""{"summary":"fine","score":1}"""));

        Answer first = await session.Request<Answer>("one");
        Answer second = await session.Request<Answer>(new Question("two", [], Level.Low));

        Assert.Equal(first, second);
        Assert.Single(await Pids());
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("args.txt"));
        Assert.Contains("--json-schema", arguments);
        Assert.Contains("--input-format", arguments);
        Assert.Equal("""{"task":"two","items":[],"level":"Low"}""", JsonDocument.Parse((await File.ReadAllLinesAsync(temp.Combine("frames.txt")))[1]).RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Ask_WhenTheOutputTypeChanges_StartsANewProcessForTheNewSchema()
    {
        await using IPromptSession session = await Session($"if grep -q summary \"$PWD/args.txt\"; then {Structured("""{"summary":"fine","score":1}""")}; else {Structured("""{"task":"t","items":[],"level":"Low"}""")}; fi");

        await session.Request<Answer>("one");
        int first = (await Pids())[0];
        Question question = await session.Request<Question>("two");

        Assert.Equal("t", question.Task);
        Assert.Empty(question.Items);
        Assert.Equal(Level.Low, question.Level);
        Assert.Equal(2, (await Pids()).Length);
        await WaitFor(() => !Alive(first));
        Assert.False(Alive(first));
    }
}
