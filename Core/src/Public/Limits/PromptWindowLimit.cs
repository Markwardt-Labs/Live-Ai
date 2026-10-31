namespace Markwardt.LiveAi;

/// <summary>
/// A limit on how much of the subscription a session may use up: the windows of the provider's limits are read at an interval while a request runs, and the request is stopped when the function says the limit is reached. See <see cref="PromptSessionLimits.WindowLimit" />.
/// </summary>
public sealed record PromptWindowLimit
{
    /// <summary>
    /// Gets a function that is given the limit windows and says whether the limit is reached, for example <c>windows.Any(window =&gt; window.Used &gt; 0.9)</c>, or a pace over 1 on the window that matters. It is called in the order the reads finish and never at the same time.
    /// </summary>
    public required Func<IReadOnlyList<PromptWindow>, bool> Reached { get; init; }

    /// <summary>
    /// Gets how often the windows are read while a request runs. The default is one minute; it must be positive and at most 24 days.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    internal bool IsValid
        => Interval > TimeSpan.Zero && Interval <= TimeSpan.FromDays(24);
}
