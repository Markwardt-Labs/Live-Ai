namespace Markwardt.LiveAi;

/// <summary>
/// The tool reported the state of the subscription's limits while it worked (Claude Code does so with every prompt).
/// </summary>
public sealed record PromptWindowsEvent : PromptEvent
{
    /// <summary>
    /// Gets the limit windows with how much of each is used.
    /// </summary>
    public required IReadOnlyList<PromptWindow> Windows { get; init; }
}
