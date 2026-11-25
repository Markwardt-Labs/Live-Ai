namespace Markwardt.LiveAi;

/// <summary>
/// The AI wrote some more of its reasoning. It is not part of the answer, but for a model that reasons for minutes before it writes anything it is the only sign that the work goes on, so a caller that shows progress can take these and one that does not can ignore them. OpenCode reports the reasoning itself, Codex a summary of it (the library asks for one), and Claude Code only when its model returns the text, which it often does not in the mode the library runs it in (the thinking arrives as an empty block, which is reported as a <see cref="PromptNoticeEvent" /> instead).
/// </summary>
public sealed record PromptThinkingEvent : PromptEvent
{
    /// <summary>
    /// Gets the reasoning that was written since the last event of this kind.
    /// </summary>
    public required string Text { get; init; }
}
