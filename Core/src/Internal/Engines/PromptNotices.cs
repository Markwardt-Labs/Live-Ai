namespace Markwardt.LiveAi;

/// <summary>
/// The wording of the notices the engines report (<see cref="PromptNoticeEvent" />) when their tool does something that takes a while and writes nothing, so every tool says the same thing about the same event.
/// </summary>
internal sealed record PromptNotices
{
    /// <summary>
    /// Gets the notice for the tool summarizing the conversation because the context window is full.
    /// </summary>
    public string Compacting { get; } = "The context window is full: the conversation is being summarized to make room, which can take a while.";

    /// <summary>
    /// Gets the notice for the model running out of output tokens and being asked to go on.
    /// </summary>
    public string Continuing { get; } = "The model ran out of output tokens before it finished its answer and is asked to go on.";

    /// <summary>
    /// Gets the notice for a model that reasons while its tool does not report what it reasons.
    /// </summary>
    public string Thinking { get; } = "The AI is thinking; its tool does not report what it thinks.";
}
