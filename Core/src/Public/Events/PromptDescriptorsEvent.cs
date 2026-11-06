namespace Markwardt.LiveAi;

/// <summary>
/// What the session says about itself changed, which happens when the model chosen for it changes. It is only reported on <see cref="IPromptSession" />.
/// </summary>
public sealed record PromptDescriptorsEvent : PromptEvent
{
    /// <summary>
    /// Gets the descriptors the session has now, as <see cref="IPromptSession.Descriptors" /> reads them.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Descriptors { get; init; }
}
