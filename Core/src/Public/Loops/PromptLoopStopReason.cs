namespace Markwardt.LiveAi;

/// <summary>
/// Why a prompt loop stopped, or that it has not.
/// </summary>
public enum PromptLoopStopReason
{
    /// <summary>
    /// The loop is still running.
    /// </summary>
    Running,

    /// <summary>
    /// The loop was disposed.
    /// </summary>
    Disposed,

    /// <summary>
    /// The loop ran for as long as <see cref="PromptLoopLimits{TReply}.Timeout" /> allows.
    /// </summary>
    Timeout,

    /// <summary>
    /// The loop sent as many times as <see cref="PromptLoopLimits{TReply}.MaxRuns" /> allows.
    /// </summary>
    MaxRuns,

    /// <summary>
    /// <see cref="PromptLoopLimits{TReply}.Until" /> returned <see langword="true" /> for a reply.
    /// </summary>
    Until,

    /// <summary>
    /// A subscription window reached <see cref="PromptLoopLimits{TReply}.WindowLimit" />.
    /// </summary>
    WindowLimit,

    /// <summary>
    /// The loop used what <see cref="PromptLoopLimits{TReply}.Budget" /> allows.
    /// </summary>
    Budget,

    /// <summary>
    /// A send failed, and the loop's task faulted with the error.
    /// </summary>
    Failed
}
