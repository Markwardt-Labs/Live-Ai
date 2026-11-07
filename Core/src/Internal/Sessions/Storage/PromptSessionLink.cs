namespace Markwardt.LiveAi;

/// <summary>
/// What the library stores about a session: what connects its identifier to the conversation the engine's tool keeps, and the model the conversation last ran on with the values of its settings. Everything else about a session is the caller's to set again whenever the session is opened.
/// </summary>
internal sealed record PromptSessionLink
{
    /// <summary>
    /// Gets the version of the layout of the stored file, which the store writes as the current one every time and checks when it reads (see <see cref="IPromptSessionMigration" />).
    /// </summary>
    public int Version { get; init; }

    /// <summary>
    /// Gets the identifier the library gave the session.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets when the session was last loaded. It starts as the time the session was created (or branched) and is set again each time it is loaded. A file stored before the library recorded the time gets the time it was last written.
    /// </summary>
    public required DateTimeOffset LastLoaded { get; init; }

    /// <summary>
    /// Gets which provider the session runs on, stored as <c>claude</c>, <c>codex</c> or <c>local</c>, which says whose identifier <see cref="AiSessionId" /> is.
    /// </summary>
    public required PromptProvider Provider { get; init; }

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
    /// Gets a value indicating whether the AI's file tools can use any path on the machine and not only the ones inside the folder of <see cref="WorkspacePath" />.
    /// </summary>
    public bool IsSystemAccessAllowed { get; init; }

    /// <summary>
    /// Gets the identifier of the engine's session that this session branches from, until the engine has made the branch's own session at the first prompt and reported its identifier.
    /// </summary>
    public string? ForkedFrom { get; init; }

    /// <summary>
    /// Gets the message of <see cref="ForkedFrom" /> the branch continues from, or <see langword="null" /> to continue from its end.
    /// </summary>
    public string? ForkedAt { get; init; }

    /// <summary>
    /// Gets the setup the session was created with, which says which tool runs it and holds what that tool needs of the session (the API key, and for local models the overrides of the server and models), so that loading it needs only its identifier. It is stored under the name <c>options</c>. A session stored without it cannot be loaded.
    /// </summary>
    [JsonPropertyName("options")]
    public PromptSetup? Setup { get; init; }

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
    /// Gets a value indicating whether the AI can make images with the image generation of the environment.
    /// </summary>
    public bool IsImageGenerationAllowed { get; init; }

    /// <summary>
    /// Gets how capable the model of the session is. The model itself is chosen again from it when a prompt starts.
    /// </summary>
    public PromptCapability Capability { get; init; } = PromptCapability.Balanced;

    /// <summary>
    /// Gets how much effort the model puts into each prompt.
    /// </summary>
    public PromptEffort Effort { get; init; } = PromptEffort.High;

    /// <summary>
    /// Makes the in-memory description of a session that was just opened: its link, and the settings every session starts with.
    /// </summary>
    /// <param name="engine">The command line tool the session runs on.</param>
    /// <returns>The record, with the stored model as one the tool could not be asked about, and no instructions or servers.</returns>
    public PromptSessionRecord ToRecord(IPromptEngine engine)
        => new()
        {
            Id = Id,
            LastLoaded = LastLoaded,
            Engine = engine,
            Setup = Setup,
            Capability = Capability,
            Effort = Effort,
            IsShellAllowed = IsShellAllowed,
            IsWebAllowed = IsWebAllowed,
            Instructions = Instructions,
            IsImageGenerationAllowed = IsImageGenerationAllowed,
            WorkingFolder = WorkingFolder,
            WorkspacePath = WorkspacePath,
            IsSystemAccessAllowed = IsSystemAccessAllowed,
            AiSessionId = AiSessionId,
            ForkedFrom = ForkedFrom,
            ForkedAt = ForkedAt
        };
}
