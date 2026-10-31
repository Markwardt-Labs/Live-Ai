namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptLoopScheduleTests
{
    [Fact]
    public void Manual_NeverRepeatsOnItsOwn()
    {
        Assert.Equal(PromptLoopMode.Manual, PromptLoopSchedule.Manual.Mode);
        Assert.Equal(TimeSpan.Zero, PromptLoopSchedule.Manual.Interval);
    }

    [Fact]
    public void Continuous_RepeatsAtOnceAndHasNoInterval()
    {
        Assert.Equal(PromptLoopMode.Continuous, PromptLoopSchedule.Continuous.Mode);
        Assert.Equal(TimeSpan.Zero, PromptLoopSchedule.Continuous.Interval);
    }

    [Fact]
    public void Every_KeepsTheIntervalAndTheIntervalMode()
    {
        PromptLoopSchedule schedule = PromptLoopSchedule.Every(TimeSpan.FromSeconds(5));

        Assert.Equal(PromptLoopMode.Interval, schedule.Mode);
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.Interval);
    }

    [Fact]
    public void Every_AcceptsZeroAndAnyLengthButNotANegativeInterval()
    {
        Assert.Equal(PromptLoopMode.Interval, PromptLoopSchedule.Every(TimeSpan.Zero).Mode);
        Assert.Equal(TimeSpan.MaxValue, PromptLoopSchedule.Every(TimeSpan.MaxValue).Interval);
        Assert.Throws<ArgumentOutOfRangeException>(() => PromptLoopSchedule.Every(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Schedules_AreEqualWhenModeAndIntervalAre()
    {
        Assert.Equal(PromptLoopSchedule.Every(TimeSpan.FromSeconds(1)), PromptLoopSchedule.Every(TimeSpan.FromSeconds(1)));
        Assert.NotEqual(PromptLoopSchedule.Every(TimeSpan.FromSeconds(1)), PromptLoopSchedule.Every(TimeSpan.FromSeconds(2)));
        Assert.NotEqual(PromptLoopSchedule.Manual, PromptLoopSchedule.Continuous);
        Assert.NotEqual(PromptLoopSchedule.Continuous, PromptLoopSchedule.Every(TimeSpan.Zero));
        Assert.Equal(PromptLoopSchedule.Continuous, default);
    }
}
