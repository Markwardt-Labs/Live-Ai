namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptDescriptorsTests
{
    [Fact]
    public void WithoutAModel_OnlyTheProviderIsDescribed()
        => Assert.Equal(new Dictionary<string, string> { ["Provider"] = "Claude Code" }, PromptDescriptors.Describe("Claude Code", null, new Dictionary<string, string>()));

    [Fact]
    public void TheModelIsNamedByItsDisplayNameWithItsIdentifierBesideItOnlyWhenTheyDiffer()
    {
        IReadOnlyDictionary<string, string> named = PromptDescriptors.Describe("Claude Code", new PromptModel { Id = "claude-opus-4-7", Name = "Opus 4.7" }, new Dictionary<string, string>());
        IReadOnlyDictionary<string, string> same = PromptDescriptors.Describe("Codex", new PromptModel { Id = "gpt-5.6-sol", Name = "gpt-5.6-sol" }, new Dictionary<string, string>());
        IReadOnlyDictionary<string, string> unnamed = PromptDescriptors.Describe("OpenCode", new PromptModel { Id = "ollama/gemma4:26b" }, new Dictionary<string, string>());

        Assert.Equal(("Opus 4.7", "claude-opus-4-7"), (named["Model"], named["Model ID"]));
        Assert.Equal("gpt-5.6-sol", same["Model"]);
        Assert.DoesNotContain("Model ID", same.Keys);
        Assert.Equal("ollama/gemma4:26b", unnamed["Model"]);
        Assert.DoesNotContain("Model ID", unnamed.Keys);
    }

    [Fact]
    public void SettingsAreNamedAsTheToolNamesThemAndTheirValuesAreWrittenForReading()
    {
        PromptModel model = new()
        {
            Id = "m",
            Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "high"]), PromptModelSetting.Count("context", "Context window", 1024, 262144, 4096), PromptModelSetting.Switch("fast", "Fast mode")]
        };

        IReadOnlyDictionary<string, string> described = PromptDescriptors.Describe("T", model, new Dictionary<string, string> { ["effort"] = "high", ["context"] = "16384", ["fast"] = "true" });

        Assert.Equal(("High", "16,384", "On"), (described["Effort"], described["Context window"], described["Fast mode"]));
        Assert.Equal(["Provider", "Model", "Context window", "Effort", "Fast mode"], described.Keys);
    }

    [Theory]
    [InlineData("reasoning_level", "Reasoning level")]
    [InlineData("contextSize", "Context size")]
    [InlineData("deep-think", "Deep think")]
    public void ASettingTheModelDoesNotDescribeGetsAReadableNameFromItsIdentifier(string id, string expected)
    {
        IReadOnlyDictionary<string, string> described = PromptDescriptors.Describe("T", new PromptModel { Id = "m" }, new Dictionary<string, string> { [id] = "false" });

        Assert.Equal("Off", described[expected]);
    }

    [Fact]
    public void ASettingWhoseNameIsTakenIsNotLost()
    {
        PromptModel model = new() { Id = "m", Settings = [PromptModelSetting.Choose("x", "Model", ["a"])] };

        IReadOnlyDictionary<string, string> described = PromptDescriptors.Describe("T", model, new Dictionary<string, string> { ["x"] = "a" });

        Assert.Equal(("m", "A"), (described["Model"], described["Model setting"]));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "A")]
    [InlineData("high", "High")]
    [InlineData("Max", "Max")]
    public void Capitalized_UpperCasesTheFirstLetterOnly(string text, string expected)
        => Assert.Equal(expected, PromptDescriptors.Capitalized(text));
}
