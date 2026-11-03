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
    /// Gets the identifier of the model to run the prompt on (<see cref="PromptModel.Id" />).
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets the values of the model's settings to run the prompt with, by <see cref="PromptModelSetting.Id" />.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the notes to put in front of the prompt, which are for the AI and not part of what the user said.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
