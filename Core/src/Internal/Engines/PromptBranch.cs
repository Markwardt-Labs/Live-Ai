namespace Markwardt.LiveAi;

/// <summary>
/// Where a branch of a conversation is, as the engine made it.
/// </summary>
internal sealed record PromptBranch
{
    /// <summary>
    /// Gets the engine's session of the branch when the engine made it at once, or <see langword="null" /> when it makes it at the first prompt.
    /// </summary>
    public string? AiSessionId { get; init; }

    /// <summary>
    /// Gets the engine's session the branch is made from at its first prompt, or <see langword="null" /> when <see cref="AiSessionId" /> is set.
    /// </summary>
    public string? ForkedFrom { get; init; }

    /// <summary>
    /// Gets the message of <see cref="ForkedFrom" /> the branch continues from, or <see langword="null" /> for its end.
    /// </summary>
    public string? ForkedAt { get; init; }
}
