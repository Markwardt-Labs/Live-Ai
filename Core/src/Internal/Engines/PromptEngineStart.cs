namespace Markwardt.LiveAi;

/// <summary>
/// What an <see cref="IPromptEngine" /> needs to describe the process that runs one prompt.
/// </summary>
internal sealed record PromptEngineStart
{
    /// <summary>
    /// Gets the identifier of the model to run (<see cref="PromptModel.Id" />), or <see langword="null" /> for a process that runs no model, such as one that only reads or deletes a stored conversation.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the values of the model's settings, by <see cref="PromptModelSetting.Id" />.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets a value indicating whether the tool may use its own web search, which only an engine with <see cref="IPromptEngine.NativeSearch" /> is asked to allow.
    /// </summary>
    public bool Search { get; init; }

    /// <summary>
    /// Gets the folder the process runs in, which is empty and made for the session. A tool that keeps its sessions by folder needs it as the working directory.
    /// </summary>
    public required string WorkingFolder { get; init; }

    /// <summary>
    /// Gets the identifier of the tool's own session to continue, or <see langword="null" /> for the first prompt.
    /// </summary>
    public string? AiSessionId { get; init; }

    /// <summary>
    /// Gets the identifier of a session to continue as a copy of, when the tool branches by itself and <see cref="IPromptEngine.Branch" /> returned <see langword="null" />; <see cref="AiSessionId" /> is <see langword="null" /> then.
    /// </summary>
    public string? ForkedFrom { get; init; }

    /// <summary>
    /// Gets the message of <see cref="ForkedFrom" /> the branch continues from, or <see langword="null" /> to continue from its end.
    /// </summary>
    public string? ForkedAt { get; init; }

    /// <summary>
    /// Gets the API key to run with, or <see langword="null" /> for the sign in the tool already has. It belongs in the environment, never on a command line.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets what the AI is told about how it can work and, after that, the custom instructions of the session. Pass it as a system or developer instruction.
    /// </summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// Gets the JSON Schema the answer must follow (its root is an object), or <see langword="null" /> for an answer in plain text.
    /// </summary>
    public JsonElement? Schema { get; init; }

    /// <summary>
    /// Gets the MCP servers to give the tool; none when the AI has no tools.
    /// </summary>
    public IReadOnlyList<PromptEngineServer> Servers { get; init; } = [];

    /// <summary>
    /// Gets the paths of the images written as files for this prompt, in the order they were attached; they are only there when <see cref="IPromptEngine.Accepts" /> said the tool takes the type.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// Describes the members for <see cref="object.ToString" />, with the API key hidden, so it does not end up in a log or a failure message.
    /// </summary>
    /// <param name="builder">Receives the text.</param>
    /// <returns><see langword="true" />, since members were written.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Model = ").Append(Model);
        builder.Append(", Settings = ").Append(string.Join(", ", Settings.Select(setting => $"{setting.Key}={setting.Value}")));
        builder.Append(", Search = ").Append(Search);
        builder.Append(", WorkingFolder = ").Append(WorkingFolder);
        builder.Append(", AiSessionId = ").Append(AiSessionId);
        builder.Append(", ForkedFrom = ").Append(ForkedFrom);
        builder.Append(", ForkedAt = ").Append(ForkedAt);
        builder.Append(", ").Append("ApiKey = ").Append(ApiKey is null ? string.Empty : "***");
        builder.Append(", Schema = ").Append(Schema is null ? string.Empty : "...");
        builder.Append(", Servers = ").Append(Servers.Count);
        builder.Append(", Files = ").Append(Files.Count);
        return true;
    }
}
