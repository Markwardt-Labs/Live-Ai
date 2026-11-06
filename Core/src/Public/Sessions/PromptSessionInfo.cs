namespace Markwardt.LiveAi;

/// <summary>
/// What is known about a stored session without opening it, as <see cref="IPrompter.List" /> returns it.
/// </summary>
public sealed record PromptSessionInfo
{
    /// <summary>
    /// Gets the identifier of the session, which <see cref="IPrompter.Load" /> and <see cref="IPrompter.Delete" /> take.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name of the tool the session runs on, such as <c>Claude Code</c>, <c>Codex</c> or <c>OpenCode</c> (which runs the local models).
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Gets when the session was created, or <see langword="null" /> for one that was stored before the library recorded it.
    /// </summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>
    /// Gets the full path of the workspace folder the session works in: the one it was given, or the one the library made for it in its data folder.
    /// </summary>
    public required string WorkspacePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the tool has a conversation for the session, which is so once the first prompt has started. A session that is not started has nothing to resume.
    /// </summary>
    public bool IsStarted { get; init; }
}
