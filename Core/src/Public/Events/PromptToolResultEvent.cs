namespace Markwardt.LiveAi;

/// <summary>
/// A tool call finished and the AI got its result.
/// </summary>
public sealed record PromptToolResultEvent : PromptEvent
{
    /// <summary>
    /// Gets the identifier of the call, from <see cref="PromptToolCallEvent.Id" />.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the text the tool returned, or the error it reported.
    /// </summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the call failed.
    /// </summary>
    public bool IsError { get; init; }
}
