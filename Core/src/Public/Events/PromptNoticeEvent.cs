namespace Markwardt.LiveAi;

/// <summary>
/// The tool did something on its own that takes a while and shows nothing else, such as summarizing the conversation to make room in the context window, or asking the model to go on after it ran out of output tokens. It is meant to be shown to the user as a short note.
/// </summary>
public sealed record PromptNoticeEvent : PromptEvent
{
    /// <summary>
    /// Gets what the tool is doing, as a sentence.
    /// </summary>
    public required string Text { get; init; }
}
