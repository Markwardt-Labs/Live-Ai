namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptWindowTests
{
    private readonly DateTimeOffset reset = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private PromptWindow Window(double used, double daysPassed = 3.5, TimeSpan? length = null, DateTimeOffset? resets = null)
        => new() { Name = "Week", Used = used, Length = length ?? TimeSpan.FromDays(7), ReadAt = reset - TimeSpan.FromDays(7) + TimeSpan.FromDays(daysPassed), ResetsAt = resets ?? reset };

    [Theory]
    [InlineData(0.5, 3.5, 1)]
    [InlineData(0.25, 3.5, 0.5)]
    [InlineData(1, 3.5, 2)]
    [InlineData(0, 3.5, 0)]
    [InlineData(0.7, 7, 0.7)]
    public void Pace_IsTheShareUsedOverTheShareOfTheWindowThatHadPassedWhenItWasRead(double used, double daysPassed, double expected)
        => Assert.Equal(expected, Window(used, daysPassed).Pace!.Value, 6);

    [Fact]
    public void Pace_OfAWindowReadAfterTheReset_CountsTheWholeWindow()
        => Assert.Equal(0.4, Window(0.4, 10).Pace!.Value, 6);

    [Fact]
    public void Pace_OfAWindowReadAtOrBeforeItsStart_HasNoPaceYet()
    {
        Assert.Null(Window(0.05, 0).Pace);
        Assert.Null(Window(0.05, -1).Pace);
    }

    [Fact]
    public void Pace_WithoutAResetTime_HasNoPace()
        => Assert.Null(new PromptWindow { Name = "Session", Used = 0.3, Length = TimeSpan.FromHours(5), ReadAt = reset }.Pace);

    [Fact]
    public void Pace_WithoutALength_HasNoPace()
        => Assert.Null(Window(0.3, 1, TimeSpan.Zero).Pace);

    [Fact]
    public void Pace_DoesNotDependOnTheClock()
    {
        DateTimeOffset longAgo = DateTimeOffset.UtcNow.AddYears(-1);
        PromptWindow old = new() { Name = "Session", Used = 0.5, Length = TimeSpan.FromHours(10), ReadAt = longAgo.AddHours(-5), ResetsAt = longAgo };

        Assert.Equal(1, old.Pace!.Value, 6);
    }
}
