namespace Markwardt.LiveAi;

/// <summary>
/// What a prompt used, as the provider's tool reports it when the prompt is done.
/// </summary>
public sealed record PromptUsage
{
    /// <summary>
    /// Gets the tokens the AI read, not counting those it read from its cache.
    /// </summary>
    public long InputTokens { get; init; }

    /// <summary>
    /// Gets the tokens the AI read from its cache.
    /// </summary>
    public long CachedInputTokens { get; init; }

    /// <summary>
    /// Gets the tokens the AI wrote, including its reasoning.
    /// </summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// Gets the cost in US dollars, or <see langword="null" /> when the tool does not report one (Codex does not).
    /// </summary>
    public decimal? CostUsd { get; init; }

    /// <summary>
    /// Adds the usage of another prompt to this one.
    /// </summary>
    /// <param name="other">The other usage.</param>
    /// <returns>The sum; its cost is unknown only when both costs are.</returns>
    internal PromptUsage Plus(PromptUsage other)
        => new()
        {
            InputTokens = InputTokens + other.InputTokens,
            CachedInputTokens = CachedInputTokens + other.CachedInputTokens,
            OutputTokens = OutputTokens + other.OutputTokens,
            CostUsd = CostUsd is null && other.CostUsd is null ? null : (CostUsd ?? 0) + (other.CostUsd ?? 0)
        };

    /// <summary>
    /// Takes the usage of an earlier point away from this one.
    /// </summary>
    /// <param name="earlier">The usage at the earlier point.</param>
    /// <returns>What was used since; its cost is unknown only when both costs are.</returns>
    internal PromptUsage Minus(PromptUsage earlier)
        => new()
        {
            InputTokens = InputTokens - earlier.InputTokens,
            CachedInputTokens = CachedInputTokens - earlier.CachedInputTokens,
            OutputTokens = OutputTokens - earlier.OutputTokens,
            CostUsd = CostUsd is null && earlier.CostUsd is null ? null : (CostUsd ?? 0) - (earlier.CostUsd ?? 0)
        };
}
