namespace Markwardt.LiveAi;

/// <summary>
/// When a prompt loop stops by itself. Every limit is optional, and the loop stops at the first one it reaches (see <see cref="IPromptLoop{TReply}.StopReason" />), normally and not with an error.
/// </summary>
/// <typeparam name="TReply">The type of the loop's replies.</typeparam>
public sealed record PromptLoopLimits<TReply>
{
    /// <summary>
    /// Gets how long the loop may run, from when it is made, at most 24 days. A send in progress when the time is up is cancelled.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets how many sends the loop makes. The reply of the last one is still published.
    /// </summary>
    public int? MaxRuns { get; init; }

    /// <summary>
    /// Gets a function that is given each reply, after it is published, and says whether the loop is done.
    /// </summary>
    public Func<TReply, bool>? Until { get; init; }

    /// <summary>
    /// Gets how much the loop may use in all, counting what its sends use. The loop stops after the send that reaches it, and publishes that reply.
    /// </summary>
    public PromptBudget? Budget { get; init; }

    /// <summary>
    /// Gets a limit on the subscription (see <see cref="PromptSessionLimits.WindowLimit" />): its function is called before each send and on its interval while one runs, and the loop stops without an error when it returns <see langword="true" />. Reading the windows starts a process of the tool, so the default interval is one minute. It replaces the session's window limit while the loop runs.
    /// </summary>
    public PromptWindowLimit? WindowLimit { get; init; }

    /// <summary>
    /// Gets a function that chooses the window to wait out when <see cref="WindowLimit" /> is reached, so that the loop waits for it to reset and then sends again, instead of stopping, or <see langword="null" /> to stop. It is given the windows that reached the limit and returns the one whose reset ends the wait (for example the session of Claude Code, or the one with the latest reset among those over a share): only the caller knows which window its limit was about, and the windows of a plan include ones that have nothing to do with it. The loop waits until the reset of the window it returns (a margin later), and then checks <see cref="WindowLimit" /> again, so it keeps waiting while a limit still holds. <see cref="IPromptLoop{TReply}.ResumesAt" /> says until when it waits. The wait counts against <see cref="Timeout" />, and the loop stops with <see cref="PromptLoopStopReason.WindowLimit" /> as usual when the function returns <see langword="null" /> or a window without a reset time in the future. It needs a <see cref="WindowLimit" />.
    /// </summary>
    public Func<IReadOnlyList<PromptWindow>, PromptWindow?>? WaitForWindow { get; init; }
}
