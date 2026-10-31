namespace Markwardt.LiveAi;

/// <summary>
/// What goes along with an input to a session, whether it is given with <see cref="IPromptSession.Send" /> or asked with <see cref="IPromptSession.Request(object, PromptRequestOptions?, CancellationToken)" /> (whose <see cref="PromptRequestOptions" /> derive from this record and add what only a request has).
/// </summary>
public record PromptOptions
{
    /// <summary>
    /// Gets the files that go with the input. Images are shown to the AI, and so are PDF documents with Claude Code; text files are put into the prompt as text; anything else is refused when the input is given.
    /// </summary>
    public IReadOnlyList<PromptAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Gets what to tell the AI with the input as notes from the application and not as something the user said (that a build now passes, or that a file was changed by hand), in the order they are given. Claude Code is given each as a system reminder in front of the input, and Codex as a developer message. They stay in the conversation and are not stored apart from it. A note must not be blank.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
