namespace Markwardt.LiveAi;

/// <summary>
/// The session changed what it is doing. It is only reported on <see cref="IPromptSession" />.
/// </summary>
public sealed record PromptStateEvent : PromptEvent
{
    /// <summary>
    /// Gets the state the session is in now.
    /// </summary>
    public required PromptSessionState State { get; init; }
}
