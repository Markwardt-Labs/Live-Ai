namespace Markwardt.LiveAi;

/// <summary>
/// Everything a prompt produced, as <see cref="IPromptCollector.Collect" /> returns it.
/// </summary>
internal sealed record PromptResult
{
    /// <summary>
    /// Gets the text, as <see cref="IPromptSession.Request(object, PromptRequestOptions?, CancellationToken)" /> returns it: the messages joined, or with a schema set the last one.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets every event, in order.
    /// </summary>
    public required IReadOnlyList<PromptEvent> Events { get; init; }

    /// <summary>
    /// Gets what the prompt used, added up from its usage events; zero when the tool reported none.
    /// </summary>
    public PromptUsage Usage { get; init; } = new();
}
