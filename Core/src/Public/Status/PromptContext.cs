namespace Markwardt.LiveAi;

/// <summary>
/// How much of the AI's context window a conversation fills.
/// </summary>
public sealed record PromptContext
{
    /// <summary>
    /// Gets how many tokens the conversation takes up now, the tool's own instructions and tool descriptions included.
    /// </summary>
    public required long Tokens { get; init; }

    /// <summary>
    /// Gets how many tokens the context window of the model holds.
    /// </summary>
    public required long Limit { get; init; }

    /// <summary>
    /// Gets the part of the window that is used, from 0 (none) to 1 (all of it), so 0.5 is half, or 0 when the size of the window is not known.
    /// </summary>
    public double Used => Limit > 0 ? Math.Min(1d, (double)Tokens / Limit) : 0;
}
