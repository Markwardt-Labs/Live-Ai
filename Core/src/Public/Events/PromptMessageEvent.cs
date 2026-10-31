namespace Markwardt.LiveAi;

/// <summary>
/// The AI wrote text for the user.
/// </summary>
public sealed record PromptMessageEvent : PromptEvent
{
    /// <summary>
    /// Gets the text.
    /// </summary>
    public required string Text { get; init; }
}
