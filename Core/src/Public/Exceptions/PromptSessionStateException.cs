namespace Markwardt.LiveAi;

/// <summary>
/// The session was in a state that does not allow what was asked: input given while it is responding, or a direct request asked while it is not waiting.
/// </summary>
/// <param name="state">The state the session was in.</param>
/// <param name="message">What was refused.</param>
public sealed class PromptSessionStateException(PromptSessionState state, string message) : PromptSessionException(message)
{
    /// <summary>
    /// Gets the state the session was in.
    /// </summary>
    public PromptSessionState State { get; } = state;
}
