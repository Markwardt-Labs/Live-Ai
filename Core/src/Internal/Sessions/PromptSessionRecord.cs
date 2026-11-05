namespace Markwardt.LiveAi;

/// <summary>
/// What a session is as a tool is started for it: the link to the engine's conversation, which is stored, and the settings the tool is started with, which are not.
/// </summary>
internal sealed record PromptSessionRecord
{
    /// <summary>
    /// Gets the identifier the library gave the session.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the command line tool the session runs on.
    /// </summary>
    public required IPromptEngine Engine { get; init; }

    /// <summary>
    /// Gets the selected model, or <see langword="null" /> while none is. Its identifier is stored.
    /// </summary>
    public PromptModel? Model { get; init; }

    /// <summary>
    /// Gets the values of the model's settings, which are stored.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelSettings { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets a value indicating whether the tool may use its own web search, which is when the session enables search and the engine has one.
    /// </summary>
    public bool NativeSearch { get; init; }

    /// <summary>
    /// Gets the custom instructions for the AI, or <see langword="null" /> for none.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Gets the external MCP servers, by name.
    /// </summary>
    public IReadOnlyDictionary<string, PromptMcpServer> McpServers { get; init; } = new Dictionary<string, PromptMcpServer>();

    /// <summary>
    /// Gets the identifier of the engine's session that this session branches from, until the engine has made the branch's own session at the first prompt and reported its identifier.
    /// </summary>
    public string? ForkedFrom { get; init; }

    /// <summary>
    /// Gets the message of <see cref="ForkedFrom" /> the branch continues from, or <see langword="null" /> to continue from its end.
    /// </summary>
    public string? ForkedAt { get; init; }

    /// <summary>
    /// Gets the full path of the folder the tools run in: an empty one the library made for the session, or the workspace folder the caller gave.
    /// </summary>
    public required string WorkingFolder { get; init; }

    /// <summary>
    /// Gets the identifier of the engine's own session, or <see langword="null" /> until the engine has reported one.
    /// </summary>
    public string? AiSessionId { get; init; }

    /// <summary>
    /// Gets the full path of the folder on disk that is the session's workspace and the folder the tools run in, or <see langword="null" /> when the session has none of its own.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the store of <see cref="WorkspacePath" /> only accepts paths inside the folder.
    /// </summary>
    public bool Sandbox { get; init; } = true;

    /// <summary>
    /// Gets when the session was created, which is stored again with every write of its link, or <see langword="null" /> when it is not known.
    /// </summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>
    /// Tells whether a process started for this record can also serve another: whether everything that is given to the tool when it starts is the same.
    /// </summary>
    /// <param name="other">The record to compare with.</param>
    /// <returns>Whether the process can be kept.</returns>
    public bool SameProcess(PromptSessionRecord other)
        => Instructions == other.Instructions && NativeSearch == other.NativeSearch && McpServers.Count == other.McpServers.Count && McpServers.All(server => other.McpServers.TryGetValue(server.Key, out PromptMcpServer? same) && same.Equals(server.Value)) && (!Engine.StartsWithModel || SameModel(other));

    /// <summary>
    /// Tells whether the other record runs the same model with the same settings.
    /// </summary>
    /// <param name="other">The record to compare with.</param>
    /// <returns>Whether the model and the values of its settings are the same.</returns>
    public bool SameModel(PromptSessionRecord other)
        => Model?.Id == other.Model?.Id && ModelSettings.Count == other.ModelSettings.Count && ModelSettings.All(setting => other.ModelSettings.TryGetValue(setting.Key, out string? same) && same == setting.Value);

    /// <summary>
    /// Takes the part that is stored.
    /// </summary>
    /// <returns>The link.</returns>
    public PromptSessionLink ToLink()
        => new() { Id = Id, Created = Created, Provider = Engine.Identifier, WorkingFolder = WorkingFolder, WorkspacePath = WorkspacePath, Sandbox = Sandbox, AiSessionId = AiSessionId, ForkedFrom = ForkedFrom, ForkedAt = ForkedAt, Model = Model?.Id, ModelSettings = Model is null ? null : new Dictionary<string, string>(ModelSettings) };

    /// <inheritdoc />
    public bool Equals(PromptSessionRecord? other)
        => other is not null && Id == other.Id && ReferenceEquals(Engine, other.Engine) && SameProcess(other) && SameModel(other) && ForkedFrom == other.ForkedFrom && ForkedAt == other.ForkedAt && WorkingFolder == other.WorkingFolder && WorkspacePath == other.WorkspacePath && Sandbox == other.Sandbox && AiSessionId == other.AiSessionId;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Id, Engine, Model?.Id, AiSessionId);
}
