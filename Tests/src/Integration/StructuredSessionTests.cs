namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class StructuredSessionTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptProvider Provider(IPromptEngine engine)
        => new PromptProvider(engine, temp.Combine("data"));

    private IPromptEngine Claude(string? claude = null)
        => new ClaudeEngine(temp.Script("claude", claude ?? "printf '%s\\n' \"$@\" > \"$PWD/claude-args.txt\"\nIFS= read -r line; printf '%s' \"$line\" > \"$PWD/claude-stdin.txt\"\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\",\"result\":\"x\",\"structured_output\":{\"summary\":\"fine\",\"score\":7,\"details\":{\"note\":\"n\"}}}'"), temp.Combine("claude-state"));

    private IPromptEngine Codex()
        => new CodexEngine(new FakeCodex(temp).Script(reply: """{"summary":"fine","score":7,"details":{"note":"n"}}"""), temp.Combine("codex-state"));

    private async Task<IPromptSession> Create(IPromptEngine provider)
        => temp.Track(await Provider(provider).Create());

    [Fact]
    public async Task Claude_AnswersWithTheStructuredOutputOfItsResult()
    {
        IPromptSession session = await Create(Claude());

        Answer answer = await session.Request<Answer>("rate it");

        Assert.Equal(new Answer("fine", 7, new Details("n")), answer);
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        using JsonDocument schema = JsonDocument.Parse(arguments[Array.IndexOf(arguments, "--json-schema") + 1]);
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("summary", out JsonElement _));
        Assert.Equal("rate it", await temp.ReadPrompt());
    }

    [Fact]
    public async Task Claude_SendsAnInputObjectAsJson()
    {
        IPromptSession session = await Create(Claude());

        await session.Request<Answer>(new Question("rate", ["a", "b"], Level.High));

        Assert.Equal("""{"task":"rate","items":["a","b"],"level":"High"}""", await temp.ReadPrompt());
    }

    [Fact]
    public async Task Codex_AnswersWithItsLastMessageUnderAStrictSchemaFile()
    {
        IPromptSession session = await Create(Codex());

        Answer answer = await session.Request<Answer>(new Question("rate", [], Level.Low));

        Assert.Equal(new Answer("fine", 7, new Details("n")), answer);
        JsonElement turn = Assert.Single(await new FakeCodex(temp).Parameters("turn/start"));
        Assert.Equal("""{"task":"rate","items":[],"level":"Low"}""", turn.GetProperty("input")[0].GetProperty("text").GetString());
        JsonElement schema = turn.GetProperty("outputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["summary", "score", "details"], schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.False(schema.GetProperty("properties").GetProperty("details").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Codex_ResumesTheThreadWithTheSchema()
    {
        IPromptSession session = await Create(Codex());
        await session.Request<Answer>("one");

        await session.Request<Answer>("two");

        FakeCodex fake = new(temp);
        Assert.Equal("codex-thread", Assert.Single(await fake.Parameters("thread/resume")).GetProperty("threadId").GetString());
        Assert.All(await fake.Parameters("turn/start"), request => Assert.True(request.TryGetProperty("outputSchema", out JsonElement _)));
    }

    [Fact]
    public async Task Ask_AfterAStructuredPrompt_RunsPlainPromptsWithoutASchema()
    {
        IPromptSession session = await Create(Codex());
        await session.Request<Answer>("one");

        await session.Request("two");

        IReadOnlyList<JsonElement> turns = await new FakeCodex(temp).Parameters("turn/start");
        Assert.True(turns[0].TryGetProperty("outputSchema", out JsonElement _));
        Assert.False(turns[1].TryGetProperty("outputSchema", out JsonElement _));
    }

    [Fact]
    public async Task Ask_WithARawSchema_ReturnsTheAnswerAsJson()
    {
        IPromptSession session = await Create(Claude());
        JsonElement schema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"}}}""").RootElement;

        JsonElement answer = JsonDocument.Parse(await session.Request("rate", new PromptRequestOptions { Schema = schema })).RootElement;

        Assert.Equal("fine", answer.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Ask_WhenTheAiAnswersInPlainText_ThrowsWithItsAnswer()
    {
        IPromptSession session = await Create(Claude("IFS= read -r line\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"I would rather not.\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\"}'"));

        PromptSessionOutputException exception = await Assert.ThrowsAsync<PromptSessionOutputException>(() => session.Request<Answer>("rate it"));

        Assert.Equal("I would rather not.", exception.Output);
    }

    [Fact]
    public async Task Ask_WhenTheAiAnswersWithTheWrongShape_Throws()
    {
        IPromptSession session = await Create(Claude("IFS= read -r line\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"s\",\"structured_output\":{\"summary\":\"no score\"}}'"));

        PromptSessionOutputException exception = await Assert.ThrowsAsync<PromptSessionOutputException>(() => session.Request<Answer>("rate it"));

        Assert.Equal("""{"summary":"no score"}""", exception.Output);
    }

    [Fact]
    public async Task Ask_WhenTheAiFails_ThrowsTheFailure()
    {
        IPromptSession session = await Create(Claude("IFS= read -r line\necho '{\"type\":\"result\",\"is_error\":true,\"result\":\"Invalid API key\"}'\nexit 1"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => session.Request<Answer>("rate it"));

        Assert.Equal("Invalid API key", exception.Message);
    }

    [Fact]
    public async Task Ask_WithAnOutputTypeThatIsNotAnObject_AsksForItInAnObjectWithOneProperty()
    {
        IPromptSession session = await Create(Claude());

        await Assert.ThrowsAsync<PromptSessionOutputException>(() => session.Request<string[]>("list"));

        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        using JsonDocument schema = JsonDocument.Parse(arguments[Array.IndexOf(arguments, "--json-schema") + 1]);
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.Equal("array", schema.RootElement.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.Equal("value", schema.RootElement.GetProperty("required")[0].GetString());
    }
}
