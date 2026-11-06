namespace Markwardt.LiveAi;

/// <summary>
/// What the library stores about a session: what connects its identifier to the conversation the engine's tool keeps, and the model the conversation last ran on with the values of its settings. Everything else about a session is the caller's to set again whenever the session is opened.
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
    /// Gets the full path of the folder on disk that is the session's workspace, which is also the folder the tools run in, or <see langword="null" /> when the library made the folder for the session (the working folder), which is then deleted with the session or when it moves.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the store of <see cref="WorkspacePath" /> only accepts paths inside the folder.
    /// </summary>
    public bool IsSandboxed { get; init; } = true;

    /// <summary>
    /// Gets the identifier of the engine's session that this session branches from, until the engine has made the branch's own session at the first prompt and reported its identifier.
    /// </summary>
    public string? ForkedFrom { get; init; }

    /// <summary>
    /// Gets the message of <see cref="ForkedFrom" /> the branch continues from, or <see langword="null" /> to continue from its end.
    /// </summary>
    public string? ForkedAt { get; init; }

    /// <summary>
    /// Gets the options the session was created with, which say which tool runs it and how that tool is found and used, so that loading it needs only its identifier. The API key is among what is stored. A session stored without them cannot be loaded.
    /// </summary>
    public PromptSessionOptions? Options { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI has a shell tool.
    /// </summary>
    public bool IsShellAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI can use the web.
    /// </summary>
    public bool IsWebAllowed { get; init; }

    /// <summary>
    /// Gets the custom instructions for the AI, or <see langword="null" /> for none.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Gets how images are made for the AI, or <see langword="null" /> when it has no tool for it. Its API key, if any, is stored with it.
    /// </summary>
    public PromptImageOptions? ImageGeneration { get; init; }

    /// <summary>
    /// Gets how capable the model of the session is. The model itself is chosen again from it when a prompt starts.
    /// </summary>
    public PromptCapability Capability { get; init; } = PromptCapability.Balanced;

    /// <summary>
    /// Gets how much effort the model puts into each prompt.
    /// </summary>
    public PromptEffort Effort { get; init; } = PromptEffort.Medium;

    /// <summary>
    /// Makes the in-memory description of a session that was just opened: its link, and the settings every session starts with.
    /// </summary>
    /// <param name="engine">The command line tool the session runs on.</param>
    /// <returns>The record, with the stored model as one the tool could not be asked about, and no instructions or servers.</returns>
    public PromptSessionRecord ToRecord(IPromptEngine engine)
        => new()
        {
            Id = Id,
            Created = Created,
            Engine = engine,
            Options = Options,
            Capability = Capability,
            Effort = Effort,
            IsShellAllowed = IsShellAllowed,
            IsWebAllowed = IsWebAllowed,
            Instructions = Instructions,
            ImageGeneration = ImageGeneration,
            WorkingFolder = WorkingFolder,
            WorkspacePath = WorkspacePath,
            IsSandboxed = IsSandboxed,
            AiSessionId = AiSessionId,
            ForkedFrom = ForkedFrom,
            ForkedAt = ForkedAt
        };
}
