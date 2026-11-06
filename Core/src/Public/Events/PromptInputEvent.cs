namespace Markwardt.LiveAi;

/// <summary>
/// Input that was given to the AI, as the provider stored it in the conversation: a prompt or a message, with files folded into the text or shown as a marker. It is part of <see cref="IPromptSession.GetHistory" /> and not of <see cref="IPromptSession" />, whose watchers gave the input themselves.
/// </summary>
public sealed record PromptInputEvent : PromptEvent
{
    /// <summary>
    /// Gets the text of the input.
    /// </summary>
    public required string Text { get; init; }
}
