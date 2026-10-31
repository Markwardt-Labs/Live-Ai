namespace Markwardt.LiveAi;

/// <summary>
/// One thing an engine's command line tool reports while it works on a prompt.
/// </summary>
public sealed record PromptEngineEvent
{
    /// <summary>
    /// Gets what happened.
    /// </summary>
    public required PromptEngineEventKind Kind { get; init; }

    /// <summary>
    /// Gets the session identifier for <see cref="PromptEngineEventKind.Session" />, the reply text for <see cref="PromptEngineEventKind.Reply" /> or the failure for <see cref="PromptEngineEventKind.Failed" />.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets what happened for <see cref="PromptEngineEventKind.Event" />.
    /// </summary>
    public PromptEvent? Event { get; init; }
}
