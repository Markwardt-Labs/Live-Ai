namespace Markwardt.LiveAi;

/// <summary>
/// When a prompt loop sends again: as soon as the last send is done (the default), on an interval, or never unless triggered. In every case <see cref="IPromptLoop{TReply}.Trigger" /> sends at once when no send is in progress.
/// </summary>
public readonly record struct PromptLoopSchedule
{
    private PromptLoopSchedule(PromptLoopMode mode, TimeSpan interval)
    {
        Mode = mode;
        Interval = interval;
    }

    /// <summary>
    /// Gets the schedule that never sends again unless the loop is triggered.
    /// </summary>
    public static PromptLoopSchedule Manual { get; } = new(PromptLoopMode.Manual, TimeSpan.Zero);

    /// <summary>
    /// Gets the schedule that sends again as soon as the last send is done.
    /// </summary>
    public static PromptLoopSchedule Continuous { get; } = new(PromptLoopMode.Continuous, TimeSpan.Zero);

    /// <summary>
    /// Gets what decides when the loop sends again.
    /// </summary>
    public PromptLoopMode Mode { get; }

    /// <summary>
    /// Gets the time from the start of one send to the start of the next, which is zero unless <see cref="Mode" /> is <see cref="PromptLoopMode.Interval" />.
    /// </summary>
    public TimeSpan Interval { get; }

    /// <summary>
    /// Makes the schedule that sends again when the last send is done and a time has passed since it started: a quick send waits out the rest of the time, a slow one is followed at once, and sends never overlap.
    /// </summary>
    /// <param name="interval">The time from the start of one send to the start of the next.</param>
    /// <returns>The schedule.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval" /> is negative.</exception>
    public static PromptLoopSchedule Every(TimeSpan interval)
        => interval < TimeSpan.Zero ? throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval cannot be negative.") : new(PromptLoopMode.Interval, interval);
}
