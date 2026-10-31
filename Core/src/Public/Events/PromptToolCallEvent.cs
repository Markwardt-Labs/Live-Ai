namespace Markwardt.LiveAi;

/// <summary>
/// The AI called a tool: one of the library's file tools, the shell, one of the caller's tools, or a tool of an external MCP server.
/// </summary>
public sealed record PromptToolCallEvent : PromptEvent
{
    /// <summary>
    /// Gets the identifier that ties the call to its <see cref="PromptToolResultEvent" />.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name of the MCP server that offers the tool (<c>liveai</c> for the library's own tools and the caller's), or <see langword="null" /> when the tool belongs to none.
    /// </summary>
    public string? Server { get; init; }

    /// <summary>
    /// Gets the name of the tool.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the arguments the AI gave, as JSON text.
    /// </summary>
    public string Input { get; init; } = "{}";

    /// <summary>
    /// Gets the arguments of the call as JSON, read from <see cref="Input" /> each time it is used.
    /// </summary>
    /// <exception cref="JsonException"><see cref="Input" /> is not valid JSON.</exception>
    public JsonElement Arguments
        => JsonDocument.Parse(Input).RootElement.Clone();
}
