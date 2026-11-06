namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptRequestTests
{
    private readonly Mock<IPromptExecutor> executor = new();

    private async IAsyncEnumerable<PromptEvent> Events(params PromptEvent[] events)
    {
        foreach (PromptEvent output in events)
        {
            await Task.Yield();
            yield return output;
        }
    }

    private PromptEvent Message(string text)
        => new PromptMessageEvent { Text = text };

    private void Answers(params string[] messages)
        => executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Events([.. messages.Select(Message)]));

    private IPromptSession? session;

    private IPromptSession Session => session ??= new ExecutorSession(executor.Object).Session;

    private PromptAttachment Png(params byte[] content)
        => new() { Name = "a.png", MediaType = "image/png", Content = content };

    private bool IsAnswerSchema(JsonElement? schema)
        => schema is { } value && value.GetProperty("properties").TryGetProperty("score", out JsonElement _);

    private void Ran(Func<PromptRun, bool> check, Times times)
        => executor.Verify(mock => mock.Execute(It.Is<PromptRun>(run => check(run)), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Ask_SendsTheTextOnlyAndJoinsTheMessages()
    {
        Answers("one", "two");

        string text = await Session.Request("hello");

        Assert.Equal("one\n\ntwo", text);
        Ran(run => run.Text == "hello" && run.Attachments.Count == 0 && run.Schema is null, Times.Once());
    }

    [Fact]
    public async Task AnObjectAsInput_IsWrittenAsJson()
    {
        Answers("ok");

        await Session.Request(new Question("rate", ["a"], Level.Low));

        Ran(run => run.Text == "{\"task\":\"rate\",\"items\":[\"a\"],\"level\":\"Low\"}", Times.Once());
    }

    [Fact]
    public async Task Attachments_AreSentInOrder()
    {
        Answers("ok");
        PromptAttachment first = Png(1);
        PromptAttachment second = Png(2);
        PromptAttachment third = Png(3);

        await Session.Request("look", new PromptRequestOptions { Attachments = [first, second, third] });

        Ran(run => run.Attachments.SequenceEqual(new[] { first, second, third }), Times.Once());
    }

    [Fact]
    public async Task AskOfAType_SendsItsSchemaAndReadsTheAnswer()
    {
        Answers("progress", """{"summary":"fine","score":3}""");

        Answer answer = await Session.Request<Answer>("rate it");

        Assert.Equal(new Answer("fine", 3), answer);
        Ran(run => run.Text == "rate it" && IsAnswerSchema(run.Schema), Times.Once());
    }

    [Fact]
    public async Task AskWithASchemaOption_ReturnsTheAnswerAsJsonText()
    {
        JsonElement schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        Answers("""{"any":[1,2]}""");

        string answer = await Session.Request("go", new PromptRequestOptions { Schema = schema });

        Assert.Equal(2, JsonDocument.Parse(answer).RootElement.GetProperty("any").GetArrayLength());
        Ran(run => run.Schema.HasValue && run.Schema.Value.GetRawText() == schema.GetRawText(), Times.Once());
    }

    [Fact]
    public async Task TheSchemaOption_SetsTheSchemaForAStreamAndAskOfATypeReplacesIt()
    {
        Answers("""{"summary":"fine","score":3}""");
        JsonElement other = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        await Session.Messages("go", new PromptRequestOptions { Schema = PromptJson.Schema(typeof(Answer)) });
        Answer answer = await Session.Request<Answer>("go", new PromptRequestOptions { Schema = other });

        Assert.Equal(new Answer("fine", 3), answer);
        Ran(run => IsAnswerSchema(run.Schema), Times.Exactly(2));
    }

    [Fact]
    public async Task AskOfAType_ThatIsNotAnObject_Throws()
    {
        Answers("""{"value":["a","b"]}""");

        string[] answer = await Session.Request<string[]>("go");

        Assert.Equal(["a", "b"], answer);
        Ran(run => run.Schema is { } schema && schema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString() == "array", Times.Once());
    }

    [Fact]
    public async Task Stream_ReportsMessagesAsTheyComeAndPassesTheCancellationToken()
    {
        Answers("a", "b");
        using CancellationTokenSource cancellation = new();
        CancellationToken passed = default;
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns((PromptRun _, CancellationToken token) =>
        {
            passed = token;
            return Events(Message("a"), Message("b"));
        });

        List<string> messages = await Session.Messages("go", null, cancellation: cancellation.Token);

        Assert.Equal(["a", "b"], messages);
        cancellation.Cancel();
        Assert.True(passed.IsCancellationRequested);
    }

    [Fact]
    public async Task Events_ReportsEverythingInOrderAndStreamKeepsOnlyTheMessages()
    {
        PromptEvent call = new PromptToolCallEvent { Id = "1", Name = "read_file", Server = "liveai" };
        PromptEvent result = new PromptToolResultEvent { Id = "1", Output = "text" };
        PromptEvent usage = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 5 } };
        PromptEvent status = new PromptWindowsEvent { Windows = TestWindows.Claude(session: 0.04) };
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Events(Message("one"), call, result, status, Message("two"), usage));

        List<PromptEvent> events = await Session.EventList("go");
        List<string> messages = await Session.Messages("go");
        string asked = await Session.Request("go");

        Assert.Equal([Message("one"), call, result, status, Message("two"), usage], events);
        Assert.Equal(["one", "two"], messages);
        Assert.Equal("one\n\ntwo", asked);
    }

    [Fact]
    public async Task Options_CanBeUsedForManyRequests()
    {
        Answers("ok");
        PromptRequestOptions options = new() { Notes = ["a note"] };

        await Session.Request("again", options);
        await Session.Request("again", options);

        Ran(run => run.Text == "again", Times.Exactly(2));
    }

    [Fact]
    public async Task Ask_WithASchema_ReturnsOnlyTheAnswerNotTheProgressNotes()
    {
        Answers("working on it", """{"summary":"fine","score":3}""");
        JsonElement schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        string answer = await Session.Request("go", new PromptRequestOptions { Schema = schema });

        Assert.Equal("""{"summary":"fine","score":3}""", answer);
    }

    [Fact]
    public async Task AskOfAType_WhenTheAiWritesNothing_FailsWithTheNoOutputError()
    {
        Answers();

        PromptSessionOutputException exception = await Assert.ThrowsAsync<PromptSessionOutputException>(() => Session.Request<Answer>("go"));

        Assert.Contains("wrote no structured output", exception.Message);
    }

    [Fact]
    public async Task Collect_ReturnsTheTextEveryEventAndTheUsageAddedUp()
    {
        PromptEvent usageOne = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 10, CachedInputTokens = 1, OutputTokens = 2, CostUsd = 0.5m } };
        PromptEvent usageTwo = new PromptUsageEvent { Usage = new PromptUsage { InputTokens = 5, OutputTokens = 3 } };
        executor.Setup(mock => mock.Execute(It.IsAny<PromptRun>(), It.IsAny<CancellationToken>())).Returns(() => Events(Message("one"), usageOne, Message("two"), usageTwo));

        PromptResult result = await Session.Result("go");

        Assert.Equal("one\n\ntwo", result.Text);
        Assert.Equal(4, result.Events.Count);
        Assert.Equal(new PromptUsage { InputTokens = 15, CachedInputTokens = 1, OutputTokens = 5, CostUsd = 0.5m }, result.Usage);
    }

    [Fact]
    public async Task Collect_WithASchema_ReturnsTheLastMessageAndWithoutUsageZero()
    {
        Answers("progress", """{"summary":"fine","score":3}""");
        JsonElement schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        PromptResult result = await Session.Result("go", new PromptRequestOptions { Schema = schema });

        Assert.Equal("""{"summary":"fine","score":3}""", result.Text);
        Assert.Equal(new PromptUsage(), result.Usage);
        Assert.Null(result.Usage.CostUsd);
    }

}
