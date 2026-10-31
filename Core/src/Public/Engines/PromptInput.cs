namespace Markwardt.LiveAi;

/// <summary>
/// One prompt as the engine's process is given it.
/// </summary>
public sealed record PromptInput
{
    /// <summary>
    /// Gets the text of the prompt.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the files and documents that come with the prompt, as they are given to a tool that takes them inline.
    /// </summary>
    public IReadOnlyList<PromptAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Gets the paths of the attachments the library wrote as files, for a tool that takes them by path.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// Gets the formatted schema the answer must follow, or <see langword="null" /> for an answer in plain text.
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>
    /// Gets the capability to run the prompt on.
    /// </summary>
    public required PromptCapability Capability { get; init; }

    /// <summary>
    /// Gets the reasoning to run the prompt with.
    /// </summary>
    public required PromptReasoning Reasoning { get; init; }

    /// <summary>
    /// Gets the notes to put in front of the prompt, which are for the AI and not part of what the user said.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
