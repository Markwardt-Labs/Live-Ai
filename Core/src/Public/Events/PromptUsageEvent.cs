namespace Markwardt.LiveAi;

/// <summary>
/// The AI finished a prompt and the tool reported what it used.
/// </summary>
public sealed record PromptUsageEvent : PromptEvent
{
    /// <summary>
    /// Gets what the prompt used.
    /// </summary>
    public required PromptUsage Usage { get; init; }
}
