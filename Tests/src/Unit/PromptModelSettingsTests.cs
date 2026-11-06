namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptModelSettingsTests
{
    private IReadOnlyList<PromptModel> Models(params string[] ids)
        => [.. ids.Select(id => new PromptModel { Id = id })];

    [Theory]
    [InlineData(PromptCapability.Fast, "haiku")]
    [InlineData(PromptCapability.Balanced, "sonnet")]
    [InlineData(PromptCapability.Strong, "opus")]
    [InlineData(PromptCapability.Max, "fable")]
    public void ChooseFor_PicksTheModelOfTheTierByItsName(PromptCapability capability, string expected)
        => Assert.Equal(expected, Models("sonnet", "fable", "haiku", "opus").ChooseFor(capability).Id);

    [Theory]
    [InlineData(PromptCapability.Fast, "haiku")]
    [InlineData(PromptCapability.Balanced, "sonnet")]
    [InlineData(PromptCapability.Strong, "fable")]
    [InlineData(PromptCapability.Max, "fable")]
    public void ChooseFor_WhenATierIsMissing_TakesTheNearestAndTheHigherOneOnATie(PromptCapability capability, string expected)
        => Assert.Equal(expected, Models("haiku", "sonnet", "fable").ChooseFor(capability).Id);

    [Theory]
    [InlineData(PromptCapability.Fast, "a")]
    [InlineData(PromptCapability.Balanced, "b")]
    [InlineData(PromptCapability.Strong, "c")]
    [InlineData(PromptCapability.Max, "d")]
    public void ChooseFor_WithNamesItDoesNotKnow_SpreadsTheCapabilitiesOverTheListInOrder(PromptCapability capability, string expected)
        => Assert.Equal(expected, Models("a", "b", "c", "d").ChooseFor(capability).Id);

    [Fact]
    public void ChooseFor_WithOneModel_TakesItForEveryCapability()
    {
        foreach (PromptCapability capability in Enum.GetValues<PromptCapability>())
        {
            Assert.Equal("only", Models("only").ChooseFor(capability).Id);
        }
    }

    [Fact]
    public void ChooseFor_LeavesOutModelsThatCannotCallTools()
    {
        IReadOnlyList<PromptModel> models = [new PromptModel { Id = "haiku", CallsTools = false }, new PromptModel { Id = "sonnet" }];

        Assert.Equal("sonnet", models.ChooseFor(PromptCapability.Fast).Id);
    }

    [Fact]
    public void ChooseFor_WithNoModelThatCallsTools_Fails()
    {
        IReadOnlyList<PromptModel> models = [new PromptModel { Id = "haiku", CallsTools = false }];

        Assert.Throws<PromptSessionException>(() => models.ChooseFor(PromptCapability.Fast));
        Assert.Throws<PromptSessionException>(() => Models().ChooseFor(PromptCapability.Fast));
    }

    [Theory]
    [InlineData("gemma4:26b")]
    [InlineData("GEMMA4:26B")]
    [InlineData("26b-model")]
    public void ChooseFor_WithAPreferredModel_TakesThatWhateverTheCapabilityIs(string preferred)
    {
        IReadOnlyList<PromptModel> models = [new PromptModel { Id = "ollama/other" }, new PromptModel { Id = "ollama/gemma4:26b", Name = "26b-model" }];

        Assert.Equal("ollama/gemma4:26b", models.ChooseFor(PromptCapability.Fast, preferred).Id);
        Assert.Equal("ollama/gemma4:26b", models.ChooseFor(PromptCapability.Max, preferred).Id);
    }

    [Fact]
    public void ChooseFor_WithAPreferredModelTheToolDoesNotOffer_Fails()
        => Assert.Throws<PromptSessionException>(() => Models("a", "b").ChooseFor(PromptCapability.Fast, "missing"));

    [Theory]
    [InlineData(PromptEffort.Low, "low")]
    [InlineData(PromptEffort.Medium, "medium")]
    [InlineData(PromptEffort.High, "high")]
    [InlineData(PromptEffort.Max, "max")]
    public void SettingsFor_PicksTheEffortChoiceOfTheSameName(PromptEffort effort, string expected)
    {
        PromptModel model = new() { Id = "m", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high", "max"])] };

        Assert.Equal(expected, model.SettingsFor(effort)["effort"]);
    }

    [Theory]
    [InlineData(PromptEffort.Low, "low")]
    [InlineData(PromptEffort.Medium, "medium")]
    [InlineData(PromptEffort.High, "high")]
    [InlineData(PromptEffort.Max, "high")]
    public void SettingsFor_WhenTheModelHasNoMax_TakesItsHighestForMax(PromptEffort effort, string expected)
    {
        PromptModel model = new() { Id = "m", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"])] };

        Assert.Equal(expected, model.SettingsFor(effort)["effort"]);
    }

    [Theory]
    [InlineData(PromptEffort.Low, "minimal")]
    [InlineData(PromptEffort.Medium, "deep")]
    [InlineData(PromptEffort.High, "deep")]
    [InlineData(PromptEffort.Max, "deepest")]
    public void SettingsFor_WithChoicesOfOtherNames_SpreadsTheEffortsOverThem(PromptEffort effort, string expected)
    {
        PromptModel model = new() { Id = "m", Settings = [PromptModelSetting.Choose("effort", "Effort", ["minimal", "deep", "deepest"])] };

        Assert.Equal(expected, model.SettingsFor(effort)["effort"]);
    }

    [Fact]
    public void SettingsFor_AlsoMapsTheVariantOfAnOpenCodeModel()
    {
        PromptModel model = new() { Id = "fake/model", Settings = [PromptModelSetting.Choose("variant", "Reasoning", ["low", "medium", "high"])] };

        Assert.Equal("high", model.SettingsFor(PromptEffort.High)["variant"]);
    }

    [Fact]
    public void SettingsFor_GivesTheOtherSettingsTheirDefaultsAndKeepsTheOverridesThatTheModelAccepts()
    {
        PromptModel model = new() { Id = "m", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "high"]), PromptModelSetting.Count("context", "Context", 1024, 8192, 4096), PromptModelSetting.Switch("fast", "Fast")] };

        Dictionary<string, string> settings = model.SettingsFor(PromptEffort.Low, new Dictionary<string, string> { ["context"] = "2048", ["unknown"] = "x" });

        Assert.Equal(["context", "effort", "fast"], settings.Keys.Order());
        Assert.Equal(("2048", "low", "false"), (settings["context"], settings["effort"], settings["fast"]));
    }

    [Fact]
    public void SettingsFor_AModelWithoutSettings_HasNone()
        => Assert.Empty(new PromptModel { Id = "m" }.SettingsFor(PromptEffort.High));
}
