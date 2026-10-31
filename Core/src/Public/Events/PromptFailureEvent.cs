namespace Markwardt.LiveAi;

/// <summary>
/// The work of the session failed. The session itself goes on and returns to waiting, so this is reported on <see cref="IPromptSession.Output" /> and does not end it. A request that was asked directly or in a fork also gets the exception.
/// </summary>
public sealed record PromptFailureEvent : PromptEvent
{
    /// <summary>
    /// Gets what went wrong.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the exception behind the failure.
    /// </summary>
    public Exception? Exception { get; init; }
}
