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
    /// Gets which provider the session runs on.
    /// </summary>
    public required PromptProvider Provider { get; init; }

    /// <summary>
    /// Gets when the session was last loaded, which starts as when it was created (or branched), and by which <see cref="IPrompter.List" /> orders the sessions, the most recent first.
    /// </summary>
    public required DateTimeOffset LastLoaded { get; init; }

    /// <summary>
    /// Gets the full path of the workspace folder the session works in: the one it was given, or the one the library made for it in its data folder.
    /// </summary>
    public required string WorkspacePath { get; init; }
}
