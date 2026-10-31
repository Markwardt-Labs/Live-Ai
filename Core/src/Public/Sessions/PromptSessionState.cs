namespace Markwardt.LiveAi;

/// <summary>
/// What a session is doing, which decides what can be given to it (see <see cref="IPromptSession.State" />).
/// </summary>
public enum PromptSessionState
{
    /// <summary>
    /// The AI is idle and waits for input. Anything can be given to it.
    /// </summary>
    Waiting,

    /// <summary>
    /// The AI is working on the input it was given. More input can be given at any time: it reaches the AI at once and is taken into the work.
    /// </summary>
    Working,

    /// <summary>
    /// The AI is answering a <see cref="PromptRequestMode.Direct" /> request in the main conversation. It takes no input meanwhile: giving some throws a <see cref="PromptSessionStateException" />.
    /// </summary>
    Responding
}
