namespace Markwardt.LiveAi;

/// <summary>
/// Everything an <see cref="IPromptExecutor" /> needs to run one prompt.
/// </summary>
internal sealed record PromptRun
{
    /// <summary>
    /// Gets the prompt text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the files that go along with the prompt.
    /// </summary>
    public IReadOnlyList<PromptAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Gets the notes that go with the prompt.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Gets the schema the answer must follow, or <see langword="null" /> for an answer in plain text.
    /// </summary>
    public JsonElement? Schema { get; init; }

    /// <summary>
    /// Gets how the request is run on the session.
    /// </summary>
    public PromptRequestMode Mode { get; init; }
}
