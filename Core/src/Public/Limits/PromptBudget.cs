namespace Markwardt.LiveAi;

/// <summary>
/// How much a session or a loop may use in all. The tools report what a prompt used when it ends, so a budget is checked before the next prompt starts and the prompt that passes it is finished: the overshoot is at most one prompt. Every limit is optional.
/// </summary>
public sealed record PromptBudget
{
    /// <summary>
    /// Gets how many tokens may be used, counting those the AI read (not those it read from its cache) and wrote.
    /// </summary>
    public long? MaxTokens { get; init; }

    /// <summary>
    /// Gets how much may be spent, in US dollars, where the tool reports a cost (Claude Code does; Codex does not, so this never stops it).
    /// </summary>
    public decimal? MaxCostUsd { get; init; }

    internal bool IsValid
        => MaxTokens is null or > 0 && MaxCostUsd is null or > 0;

    internal bool IsReached(PromptUsage used)
        => MaxTokens is { } tokens && used.InputTokens + used.OutputTokens >= tokens || MaxCostUsd is { } cost && used.CostUsd >= cost;
}
