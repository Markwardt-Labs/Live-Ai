namespace Markwardt.LiveAi;

/// <summary>
/// A prompt was stopped because it reached a limit set with <see cref="PromptSessionLimits.Timeout" /> or <see cref="PromptSessionLimits.WindowLimit" />.
/// </summary>
/// <param name="limit">Which limit was reached.</param>
/// <param name="message">What happened.</param>
public sealed class PromptLimitException(PromptLimit limit, string message) : PromptSessionException(message)
{
    /// <summary>
    /// Gets which limit was reached.
    /// </summary>
    public PromptLimit Limit { get; } = limit;

    /// <summary>
    /// Gets the limit windows that reached the limit when <see cref="Limit" /> is <see cref="PromptLimit.Window" />, which says when the limits reset, or <see langword="null" /> for any other limit.
    /// </summary>
    public IReadOnlyList<PromptWindow>? Windows { get; init; }
}
