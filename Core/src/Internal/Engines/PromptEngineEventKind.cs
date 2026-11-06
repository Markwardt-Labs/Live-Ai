namespace Markwardt.LiveAi;

/// <summary>
/// The kinds of <see cref="PromptEngineEvent" />.
/// </summary>
internal enum PromptEngineEventKind
{
    /// <summary>
    /// The engine named the session that the work runs in.
    /// </summary>
    Session,

    /// <summary>
    /// The AI wrote text for the user.
    /// </summary>
    Reply,

    /// <summary>
    /// Something happened that is not text: a tool call, its result, what the prompt used, or the subscription status.
    /// </summary>
    Event,

    /// <summary>
    /// The AI finished the prompt.
    /// </summary>
    Finished,

    /// <summary>
    /// The AI reported a failure.
    /// </summary>
    Failed
}
