namespace Markwardt.LiveAi.Tests.Unit.Models;

public sealed class PromptModelSettingTests
{
    [Fact]
    public void Choose_DefaultsToMediumWhenItIsOneOfTheChoicesAndOtherwiseToTheFirst()
    {
        Assert.Equal("medium", PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"]).Default);
        Assert.Equal("high", PromptModelSetting.Choose("effort", "Effort", ["high", "max"]).Default);
        Assert.Equal("max", PromptModelSetting.Choose("effort", "Effort", ["high", "max"], "max").Default);
    }

    [Fact]
    public void Choose_KeepsTheChoicesInTheOrderTheyAreGiven()
    {
        PromptModelSetting setting = PromptModelSetting.Choose("effort", "Effort", ["low", "high", "max"]);

        Assert.Equal(("effort", "Effort", PromptModelSettingKind.Choice), (setting.Id, setting.Name, setting.Kind));
        Assert.Equal(["low", "high", "max"], setting.Choices);
    }

    [Fact]
    public void Choose_RefusesNoChoicesAndADefaultThatIsNotOne()
    {
        Assert.Throws<ArgumentException>(() => PromptModelSetting.Choose("effort", "Effort", []));
        Assert.Throws<ArgumentException>(() => PromptModelSetting.Choose("effort", "Effort", ["low"], "high"));
    }

    [Theory]
    [InlineData("low", true)]
    [InlineData("high", true)]
    [InlineData("medium", false)]
    [InlineData("", false)]
    public void AChoiceAcceptsOnlyItsChoices(string value, bool accepted)
        => Assert.Equal(accepted, PromptModelSetting.Choose("effort", "Effort", ["low", "high"]).Accepts(value));

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("True", false)]
    [InlineData("1", false)]
    public void AToggleAcceptsTrueAndFalseAsTextOnly(string value, bool accepted)
        => Assert.Equal(accepted, PromptModelSetting.Switch("fast", "Fast").Accepts(value));

    [Fact]
    public void ASwitchIsOffUnlessItIsMadeOn()
    {
        Assert.Equal("false", PromptModelSetting.Switch("fast", "Fast").Default);
        Assert.Equal("true", PromptModelSetting.Switch("fast", "Fast", true).Default);
        Assert.Equal(PromptModelSettingKind.Toggle, PromptModelSetting.Switch("fast", "Fast").Kind);
    }

    [Theory]
    [InlineData("1024", true)]
    [InlineData("8192", true)]
    [InlineData("1023", false)]
    [InlineData("8193", false)]
    [InlineData("many", false)]
    [InlineData("1.5", false)]
    public void ANumberAcceptsWholeNumbersInItsRange(string value, bool accepted)
        => Assert.Equal(accepted, PromptModelSetting.Count("context", "Context", 1024, 8192, 4096).Accepts(value));

    [Fact]
    public void ACountKeepsItsRangeAndWritesItsDefaultAsText()
    {
        PromptModelSetting setting = PromptModelSetting.Count("context", "Context", 1024, 8192, 4096);

        Assert.Equal((PromptModelSettingKind.Number, 1024, 8192, "4096"), (setting.Kind, setting.Minimum, setting.Maximum, setting.Default));
    }

    [Theory]
    [InlineData(10, 5, 7)]
    [InlineData(1, 5, 9)]
    [InlineData(1, 5, 0)]
    public void ACountRefusesAnEmptyRangeAndADefaultOutsideIt(int minimum, int maximum, int defaultValue)
        => Assert.Throws<ArgumentException>(() => PromptModelSetting.Count("context", "Context", minimum, maximum, defaultValue));

    [Fact]
    public void ASettingOfAnUndefinedKindAcceptsNothing()
        => Assert.False(new PromptModelSetting { Id = "x", Name = "X", Kind = (PromptModelSettingKind)99, Default = "a" }.Accepts("a"));
}
