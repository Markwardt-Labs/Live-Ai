namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptToolTests
{
    private JsonElement Arguments(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Create_DescribesTheToolAndItsInputSchema()
    {
        PromptTool tool = PromptTool.Create<Details, string>("note", "Notes something.", (input, _) => Task.FromResult(input.Note));

        Assert.Equal("note", tool.Name);
        Assert.Equal("Notes something.", tool.Description);
        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.True(tool.InputSchema.GetProperty("properties").TryGetProperty("note", out JsonElement _));
    }

    [Fact]
    public async Task Handler_ReadsTheArgumentsIntoTheInputAndReturnsAStringAsItIs()
    {
        PromptTool tool = PromptTool.Create<Details, string>("note", "n", (input, _) => Task.FromResult($"got {input.Note}"));

        Assert.Equal("got hello", await tool.Handler(Arguments("""{"note":"hello"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Handler_WritesOtherOutputsAsJson()
    {
        PromptTool tool = PromptTool.Create<Details, Answer>("rate", "n", (input, _) => Task.FromResult(new Answer(input.Note, 3)));

        Assert.Equal("""{"summary":"x","score":3,"details":null}""", await tool.Handler(Arguments("""{"note":"x"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Handler_PassesTheCancellationToken()
    {
        using CancellationTokenSource cancellation = new();
        CancellationToken seen = default;
        PromptTool tool = PromptTool.Create<Details, string>("note", "n", (input, token) =>
        {
            seen = token;
            return Task.FromResult("ok");
        });

        await tool.Handler(Arguments("""{"note":"x"}"""), cancellation.Token);

        Assert.Equal(cancellation.Token, seen);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"note":5}""")]
    [InlineData("null")]
    public async Task Handler_WhenTheArgumentsAreNotTheInput_Throws(string json)
        => await Assert.ThrowsAsync<JsonException>(() => PromptTool.Create<Details, string>("note", "n", (input, _) => Task.FromResult(input.Note)).Handler(Arguments(json), CancellationToken.None));

    [Fact]
    public void Create_WithAnInputTypeThatIsNotAnObject_Throws()
        => Assert.Throws<ArgumentException>(() => PromptTool.Create<string, string>("note", "n", (input, _) => Task.FromResult(input)));
}
