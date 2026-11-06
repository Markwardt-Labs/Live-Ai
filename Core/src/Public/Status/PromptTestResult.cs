namespace Markwardt.LiveAi;

/// <summary>
/// The outcome of <see cref="IPrompter.Test" />.
/// </summary>
public sealed record PromptTestResult
{
    /// <summary>
    /// Gets a value indicating whether the tool answered a prompt, so that it is installed, signed in (or given a working key) and reachable.
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// Gets why the tool is not available, or <see langword="null" /> when it is.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets how much of the subscription is used, as the tool reports it (a list of windows, such as the current session and the week), or <see langword="null" /> when the tool does not say or it could not be read (an API key has no subscription). It is only read when the tool is available.
    /// </summary>
    public IReadOnlyList<PromptWindow>? Windows { get; init; }
}
