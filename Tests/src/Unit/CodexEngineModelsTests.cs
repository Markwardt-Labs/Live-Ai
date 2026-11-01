namespace Markwardt.LiveAi.Tests.Unit;

public sealed class CodexEngineModelsTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private string Catalog(string efforts)
        => $$"""{"models":[{"slug":"custom","visibility":"list","supported_reasoning_levels":[{{string.Join(',', efforts.Split(',').Select(effort => $$"""{"effort":"{{effort}}"}"""))}}]}]}""";

    [Fact]
    public void Model_UsesTheModelsItIsGivenAndTheDefaultsForTheRest()
    {
        CodexEngine engine = new("codex", null, new PromptModels { Max = "custom-max" });

        Assert.Equal(["gpt-5.6-luna", "gpt-5.6-terra", "custom-max"], new[] { PromptCapability.Fast, PromptCapability.Balanced, PromptCapability.Max }.Select(engine.Model));
    }

    [Fact]
    public void Model_UsesTheDefaultModelForEveryCapabilityWithoutOneOfItsOwn()
    {
        CodexEngine engine = new("codex", null, new PromptModels { Default = "all", Fast = "quick" });

        Assert.Equal(["quick", "all", "all"], new[] { PromptCapability.Fast, PromptCapability.Balanced, PromptCapability.Max }.Select(engine.Model));
    }

    [Fact]
    public void CreateTestStart_RunsTheModelOfTheFastCapability()
    {
        ProcessStartInfo start = new CodexEngine("codex", null, new PromptModels { Fast = "quick" }).CreateTestStart(null);

        Assert.Equal("quick", start.ArgumentList[start.ArgumentList.IndexOf("-m") + 1]);
    }

    [Fact]
    public async Task Effort_OfAModelTheEngineChoosesNeedsNoCatalog()
    {
        CodexEngine engine = new(temp.Combine("no-such-codex"));

        Assert.Equal("max", await engine.Effort(PromptCapability.Max, PromptReasoning.Max));
        Assert.False(File.Exists(temp.Combine("catalog-runs.txt")));
    }

    [Theory]
    [InlineData("low,medium,high,xhigh", PromptReasoning.Max, "xhigh")]
    [InlineData("low,medium,high", PromptReasoning.Max, "high")]
    [InlineData("low,medium", PromptReasoning.High, "medium")]
    [InlineData("high,xhigh", PromptReasoning.Low, "high")]
    [InlineData("low,medium,high,xhigh,max", PromptReasoning.Max, "max")]
    [InlineData("low,medium,high", PromptReasoning.Medium, "medium")]
    public async Task Effort_ForAModelTheCallerChoseIsLoweredToTheHighestTheModelAccepts(string accepted, PromptReasoning reasoning, string expected)
    {
        CodexEngine engine = new(new FakeCodex(temp).Script(models: Catalog(accepted)), null, PromptModels.Only("custom"));

        Assert.Equal(expected, await engine.Effort(PromptCapability.Balanced, reasoning));
    }

    [Fact]
    public async Task Effort_ReadsTheCatalogOnceForAllTheTurns()
    {
        CodexEngine engine = new(new FakeCodex(temp).Script(models: Catalog("low,medium")), null, PromptModels.Only("custom"));

        await engine.Effort(PromptCapability.Fast, PromptReasoning.High);
        await engine.Effort(PromptCapability.Max, PromptReasoning.Max);

        Assert.Single(await File.ReadAllLinesAsync(temp.Combine("catalog-runs.txt")));
    }

    [Fact]
    public async Task Effort_IsNotLoweredWhenTheCatalogDoesNotKnowTheModelOrCannotBeRead()
    {
        CodexEngine unknown = new(new FakeCodex(temp).Script(models: Catalog("low")), null, PromptModels.Only("other"));
        CodexEngine broken = new(temp.Combine("no-such-codex"), null, PromptModels.Only("custom"));

        Assert.Equal("max", await unknown.Effort(PromptCapability.Max, PromptReasoning.Max));
        Assert.Equal("max", await broken.Effort(PromptCapability.Max, PromptReasoning.Max));
    }

    [Fact]
    public async Task Effort_StopsWhenTheCallerCancelsBeforeTheCatalogIsRead()
    {
        CodexEngine engine = new(new FakeCodex(temp).Script(models: Catalog("low")), null, PromptModels.Only("custom"));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Effort(PromptCapability.Max, PromptReasoning.Max, cancellation.Token));
    }
}
