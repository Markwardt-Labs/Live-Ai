namespace Markwardt.LiveAi;

/// <summary>
/// The outcome of <see cref="IPromptProvider.Test" />.
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
}
