namespace Markwardt.LiveAi;

/// <summary>
/// The AI wrote some more of a message that is not finished yet. The finished message follows as a <see cref="PromptMessageEvent" />, so a caller that shows text as it comes can take these and one that waits can ignore them.
/// </summary>
public sealed record PromptDeltaEvent : PromptEvent
{
    /// <summary>
    /// Gets the text that was written since the last delta.
    /// </summary>
    public required string Text { get; init; }
}
