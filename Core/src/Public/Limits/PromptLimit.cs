namespace Markwardt.LiveAi;

/// <summary>
/// The kinds of limit that stop a prompt.
/// </summary>
public enum PromptLimit
{
    /// <summary>
    /// The prompt ran longer than the timeout allows.
    /// </summary>
    Timeout,

    /// <summary>
    /// A subscription window reached the limit the caller set.
    /// </summary>
    Window,

    /// <summary>
    /// The AI made more tool calls than <see cref="PromptSessionLimits.MaxToolCalls" /> allows.
    /// </summary>
    ToolCalls,

    /// <summary>
    /// The session had already used what its <see cref="PromptBudget" /> allows.
    /// </summary>
    Budget
}
