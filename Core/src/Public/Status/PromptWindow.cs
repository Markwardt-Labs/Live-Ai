namespace Markwardt.LiveAi;

/// <summary>
/// One limit of a subscription: how much of a rolling window of time is used.
/// </summary>
public sealed record PromptWindow
{
    /// <summary>
    /// Gets what the window is called, for display: <c>Session</c>, <c>Week</c> or <c>Week (Opus)</c> for Claude Code, and its length (<c>Week</c>, <c>5 hours</c>) for Codex. It is not a stable key: the windows of a plan can change.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets how much of the limit is used, from 0 (none) to 1 (all of it), so 0.5 is half.
    /// </summary>
    public required double Used { get; init; }

    /// <summary>
    /// Gets the length of the window. Codex reports it; for Claude Code it is the length of its session (five hours) or week (seven days).
    /// </summary>
    public required TimeSpan Length { get; init; }

    /// <summary>
    /// Gets when the numbers of the window were read, which is the moment the pace of the window is worked out for: a list that is a few minutes old describes the window as it was then.
    /// </summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary>
    /// Gets when the window resets, or <see langword="null" /> when the tool did not report it in a form that could be read.
    /// </summary>
    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>
/// What can be worked out from a <see cref="PromptWindow" />.
/// </summary>
public static class PromptWindowExtensions
{
    extension(PromptWindow window)
    {
        /// <summary>
        /// Gets how fast the window was being used when it was read (<see cref="PromptWindow.ReadAt" />): the share of the limit used divided by the share of the window that had passed. 1 is exactly the pace that uses all of the limit when the window resets, 2 is twice as fast (the limit would be gone in the first half), and 0.5 is half as fast. A read after the window reset counts as the end of the window. It is <see langword="null" /> when the reset time is not known or the window had not started when it was read, since nothing can be said about its pace.
        /// </summary>
        public double? Pace
        {
            get
            {
                if (window.ResetsAt is not { } resets || window.Length <= TimeSpan.Zero)
                {
                    return null;
                }

                double passed = Math.Min((window.ReadAt - (resets - window.Length)) / window.Length, 1);
                return passed <= 0 ? null : window.Used / passed;
            }
        }
    }
}
