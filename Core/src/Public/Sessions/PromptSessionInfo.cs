namespace Markwardt.LiveAi;

/// <summary>
/// What is known about a stored session without opening it, as <see cref="IPromptProvider.List" /> returns it.
/// </summary>
public sealed record PromptSessionInfo
{
    /// <summary>
    /// Gets the identifier of the session, which <see cref="IPromptProvider.Load" /> and <see cref="IPromptProvider.Delete" /> take.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets when the session was created, or <see langword="null" /> for one that was stored before the library recorded it.
    /// </summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>
    /// Gets the full path of the workspace folder the session was created with, or <see langword="null" /> when it has none.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the tool has a conversation for the session, which is so once the first prompt has started. A session that is not started has nothing to resume.
    /// </summary>
    public bool IsStarted { get; init; }
}
