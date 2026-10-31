namespace Markwardt.LiveAi;

/// <summary>
/// How a request tries again when the AI fails, for a failure that is likely to pass: a rate limit, an overloaded service, a dropped connection. See <see cref="PromptSessionLimits.Retry" />.
/// </summary>
public sealed record PromptRetry
{
    /// <summary>
    /// Gets how many times the prompt is tried in all, the first time included, so 1 is no retry. The default is three.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Gets how long to wait before the second try. The default is two seconds.
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets what the wait is multiplied by for each further try. The default is two.
    /// </summary>
    public double BackoffFactor { get; init; } = 2;

    /// <summary>
    /// Gets the longest wait. The default is one minute.
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets a function that says, from the error, whether to try again, or <see langword="null" /> for the default: any <see cref="PromptSessionException" /> except a <see cref="PromptLimitException" />, a <see cref="PromptSessionOutputException" /> and a <see cref="PromptToolStartException" />. Other errors (a bad argument, a cancellation) are never retried.
    /// </summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    internal bool IsValid
        => MaxAttempts >= 1 && InitialDelay >= TimeSpan.Zero && MaxDelay >= TimeSpan.Zero && MaxDelay <= TimeSpan.FromDays(24) && InitialDelay <= TimeSpan.FromDays(24) && BackoffFactor >= 1 && double.IsFinite(BackoffFactor);
}
