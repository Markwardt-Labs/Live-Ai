namespace Markwardt.LiveAi.Tests.Integration;

public sealed class PromptToolServerHookTests : IAsyncLifetime
{
    private readonly HttpClient client = new();

    private readonly Mock<IPromptToolHooks> hooks = new();

    private readonly List<string> ran = [];

    private IPromptToolServer server = null!;

    public Task InitializeAsync()
    {
        PromptTool echo = PromptTool.Create<Details, string>("echo", "Echoes the note.", (input, _) =>
        {
            ran.Add(input.Note);
            return Task.FromResult($"echo: {input.Note}");
        });
        PromptTool fail = PromptTool.Create<Details, string>("fail", "Always fails.", (_, _) => throw new InvalidOperationException("it broke"));
        server = new PromptToolHost().Start([echo, fail], hooks.Object);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        client.Dispose();
    }

    private async Task<JsonElement> Call(string tool, string arguments = """{"note":"hi"}""", string meta = "")
    {
        HttpRequestMessage request = new(HttpMethod.Post, server.Endpoint.Url) { Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}{{{meta}}}}}""", Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {server.Endpoint.Token}");
        HttpResponseMessage response = await client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
    }

    private string Text(JsonElement result)
        => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task TheHooksAreGivenTheIdentifierTheAiToolSentForTheCall_OrTheOneOfTheRequest()
    {
        List<PromptToolCallEvent> calls = [];
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).Returns((PromptToolCallEvent call, CancellationToken _) =>
        {
            calls.Add(call);
            return Task.FromResult(PromptToolDecision.Allow);
        });
        hooks.Setup(mock => mock.After(It.IsAny<PromptToolCallEvent>(), It.IsAny<PromptToolResultEvent>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Call("echo", meta: ""","_meta":{"claudecode/toolUseId":"toolu_abc"}""");
        await Call("echo");

        Assert.Equal(["toolu_abc", "1"], calls.Select(call => call.Id));
        Assert.All(calls, call => Assert.Equal("liveai", call.Server));
    }

    [Fact]
    public async Task ACallThatIsAllowed_RunsAndIsReportedWithItsOutcome()
    {
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).ReturnsAsync(PromptToolDecision.Allow);
        PromptToolCallEvent? before = null;
        PromptToolResultEvent? outcome = null;
        PromptToolCallEvent? after = null;
        TimeSpan duration = TimeSpan.MinValue;
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).Returns((PromptToolCallEvent call, CancellationToken _) =>
        {
            before = call;
            return Task.FromResult(PromptToolDecision.Allow);
        });
        hooks.Setup(mock => mock.After(It.IsAny<PromptToolCallEvent>(), It.IsAny<PromptToolResultEvent>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns((PromptToolCallEvent call, PromptToolResultEvent reported, TimeSpan took, CancellationToken _) =>
        {
            after = call;
            outcome = reported;
            duration = took;
            return Task.CompletedTask;
        });

        JsonElement result = await Call("echo");

        Assert.Equal("echo: hi", Text(result));
        Assert.Equal(["hi"], ran);
        Assert.Equal("echo", before!.Name);
        Assert.Equal("hi", before.Arguments.GetProperty("note").GetString());
        Assert.Equal(before, after);
        Assert.Equal(("echo: hi", false), (outcome!.Output, outcome.IsError));
        Assert.True(duration >= TimeSpan.Zero);
        Assert.Equal(before.Id, outcome.Id);
    }

    [Fact]
    public async Task ACallThatIsRefused_DoesNotRunIsNotReportedAndTellsTheAiWhy()
    {
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).ReturnsAsync(PromptToolDecision.Deny("not on a Friday"));

        JsonElement result = await Call("echo");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("The call was not allowed: not on a Friday. Do not try to get around this; tell the user what you wanted to do, or choose another way.", Text(result));
        Assert.Empty(ran);
        hooks.Verify(mock => mock.After(It.IsAny<PromptToolCallEvent>(), It.IsAny<PromptToolResultEvent>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACallThatIsRefusedWithoutAReason_StillSaysItWasNotAllowed()
    {
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).ReturnsAsync(PromptToolDecision.Deny());

        JsonElement result = await Call("echo");

        Assert.StartsWith("The call was not allowed. Do not try", Text(result));
        Assert.Empty(ran);
    }

    [Fact]
    public async Task AHookThatThrowsBeforeTheCall_RefusesItWithTheMessage()
    {
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("policy file missing"));

        JsonElement result = await Call("echo");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("policy file missing", Text(result));
        Assert.Empty(ran);
    }

    [Fact]
    public async Task AToolThatFails_IsReportedAsAnErrorOutcome()
    {
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).ReturnsAsync(PromptToolDecision.Allow);
        PromptToolResultEvent? outcome = null;
        hooks.Setup(mock => mock.After(It.IsAny<PromptToolCallEvent>(), It.IsAny<PromptToolResultEvent>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns((PromptToolCallEvent _, PromptToolResultEvent reported, TimeSpan _, CancellationToken _) =>
        {
            outcome = reported;
            return Task.CompletedTask;
        });

        JsonElement result = await Call("fail");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("it broke", Text(result));
        Assert.Equal(("it broke", true), (outcome!.Output, outcome.IsError));
    }

    [Fact]
    public async Task AHookThatThrowsAfterTheCall_DoesNotSpoilTheAnswer()
    {
        hooks.Setup(mock => mock.Before(It.IsAny<PromptToolCallEvent>(), It.IsAny<CancellationToken>())).ReturnsAsync(PromptToolDecision.Allow);
        hooks.Setup(mock => mock.After(It.IsAny<PromptToolCallEvent>(), It.IsAny<PromptToolResultEvent>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("log is full"));

        JsonElement result = await Call("echo");

        Assert.Equal("echo: hi", Text(result));
        Assert.False(result.TryGetProperty("isError", out JsonElement error) && error.GetBoolean());
    }
}
