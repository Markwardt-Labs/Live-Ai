namespace Markwardt.LiveAi;

/// <summary>
/// The only thing the library stores about a session: what connects its identifier to the conversation the engine's tool keeps. Everything else about a session, its settings, is the caller's to set again whenever the session is opened.
/// </summary>
internal sealed record PromptSessionLink
{
    /// <summary>
    /// Gets the identifier the library gave the session.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets when the session was created, or <see langword="null" /> for a link stored before that was recorded.
    /// </summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>
    /// Gets the name of the command line tool the session runs on (<see cref="IPromptEngine.Identifier" />), which says whose identifier <see cref="AiSessionId" /> is.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Gets the full path of the folder the tools run in: an empty one the library made for the session, or the workspace folder the caller gave. The tools keep their conversations by it, so it stays the same for the life of the session.
    /// </summary>
    public required string WorkingFolder { get; init; }

    /// <summary>
    /// Gets the identifier of the engine's own session, or <see langword="null" /> until the engine has reported one.
    /// </summary>
    public string? AiSessionId { get; init; }

    /// <summary>
    /// Gets the full path of the folder on disk that is the session's workspace, which is also the folder the tools run in, or <see langword="null" /> when the session has no workspace of its own.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the store of <see cref="WorkspacePath" /> only accepts paths inside the folder.
    /// </summary>
    public bool Sandbox { get; init; } = true;

    /// <summary>
    /// Gets the identifier of the engine's session that this session branches from, until the engine has made the branch's own session at the first prompt and reported its identifier.
    /// </summary>
    public string? ForkedFrom { get; init; }

    /// <summary>
    /// Gets the message of <see cref="ForkedFrom" /> the branch continues from, or <see langword="null" /> to continue from its end.
    /// </summary>
    public string? ForkedAt { get; init; }

    /// <summary>
    /// Makes the in-memory description of a session that was just opened: its link, and the settings every session starts with.
    /// </summary>
    /// <param name="engine">The command line tool the session runs on.</param>
    /// <returns>The record, with balanced capability, medium reasoning and no instructions or servers.</returns>
    public PromptSessionRecord ToRecord(IPromptEngine engine)
        => new()
        {
            Id = Id,
            Created = Created,
            Engine = engine,
            Capability = PromptCapability.Balanced,
            Reasoning = PromptReasoning.Medium,
            WorkingFolder = WorkingFolder,
            WorkspacePath = WorkspacePath,
            Sandbox = Sandbox,
            AiSessionId = AiSessionId,
            ForkedFrom = ForkedFrom,
            ForkedAt = ForkedAt
        };
}
