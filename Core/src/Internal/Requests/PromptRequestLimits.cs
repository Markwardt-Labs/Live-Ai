namespace Markwardt.LiveAi;

/// <summary>
/// The limits a request runs under, which the session's configuration sets and a loop adds to.
/// </summary>
internal sealed record PromptRequestLimits
{
    /// <summary>
    /// Gets how long the prompt may take, or <see langword="null" /> for no limit.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets the window limit, or <see langword="null" /> for none.
    /// </summary>
    public PromptWindowLimit? WindowLimit { get; init; }

    /// <summary>
    /// Gets how a failure is tried again, or <see langword="null" /> for no retry.
    /// </summary>
    public PromptRetry? Retry { get; init; }

    /// <summary>
    /// Gets how many tool calls the AI may make, or <see langword="null" /> for no limit.
    /// </summary>
    public int? MaxToolCalls { get; init; }

    /// <summary>
    /// Replaces the window limit with the one of another set of limits, when it has one.
    /// </summary>
    /// <param name="other">The limits to take the window limit from, or <see langword="null" /> for none.</param>
    /// <returns>These limits, with the window limit and its interval of the other when it has a window limit.</returns>
    public PromptRequestLimits WithWindowOf(PromptRequestLimits? other)
        => other?.WindowLimit is { } reached ? this with { WindowLimit = reached } : this;
}
