namespace Markwardt.LiveAi;

/// <summary>
/// What stops or repeats the requests of a session: how much it may use in all, how long a request may take, how much of the subscription it may use up, how it tries again, and how many tool calls the AI may make. Set it as a whole on <see cref="IPromptSessionConfiguration.Limits" /> (change one limit with <c>with</c> on the current record); everything is optional and the default is no limit. The limits are taken when a request starts.
/// </summary>
public sealed record PromptSessionLimits
{
    /// <summary>
    /// Gets how much the session may use in all (see <see cref="IPromptSession.Usage" />), or <see langword="null" /> for no limit. When it is reached, the next prompt fails with a <see cref="PromptLimitException" /> whose limit is <see cref="PromptLimit.Budget" />, and a loop stops when it has used its own budget. Usage is counted from when the session object was made.
    /// </summary>
    public PromptBudget? Budget { get; init; }

    /// <summary>
    /// Gets how long a request may take, counted from when it is made so also the time spent waiting for the session, or <see langword="null" /> for no limit. The work is cancelled and the request fails with a <see cref="PromptLimitException" /> whose limit is <see cref="PromptLimit.Timeout" />. In a loop it applies to each send. It must be positive and at most 24 days, which is as long as a timer can count.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets the limit on how much of the subscription a request may use up, or <see langword="null" /> for no limit. The windows of the session's provider are read when a request starts and then on the limit's interval, each time with <see cref="IPromptEngine.GetWindows" />, and the limit's function says whether it is reached. When it is, the work is cancelled and the request fails with a <see cref="PromptLimitException" /> whose limit is <see cref="PromptLimit.Window" />. Windows that cannot be read (an API key has no subscription limits) fail the request with the error. In a loop it applies to each send.
    /// </summary>
    public PromptWindowLimit? WindowLimit { get; init; }

    /// <summary>
    /// Gets how a request tries again when the AI fails with something likely to pass (a rate limit, an overloaded service, a dropped connection), or <see langword="null" /> for no retry. Only a failure before the first event is retried: once something has been reported it cannot be taken back. The waits count towards <see cref="Timeout" /> and can be cut short by it or by the caller's cancellation. In a loop it applies to each send.
    /// </summary>
    public PromptRetry? Retry { get; init; }

    /// <summary>
    /// Gets how many tool calls the AI may make in one request before it is stopped, which guards against an AI that goes round in circles, or <see langword="null" /> for no limit. The work is cancelled when the call after the last allowed one is reported, which may have started by then, and the request fails with a <see cref="PromptLimitException" /> whose limit is <see cref="PromptLimit.ToolCalls" />. Zero allows none. In a loop it applies to each send.
    /// </summary>
    public int? MaxToolCalls { get; init; }

    internal string? Problem
        => Budget is { IsValid: false } ? "A budget limit must be positive."
            : Timeout is { } span && (span <= TimeSpan.Zero || span > TimeSpan.FromDays(24)) ? "A timeout must be positive and at most 24 days."
            : WindowLimit is { IsValid: false } ? "The interval of a window limit must be positive and at most 24 days."
            : Retry is { IsValid: false } ? "A retry needs at least one attempt, waits of 24 days at most that are not negative, and a factor of at least one."
            : MaxToolCalls < 0 ? "The number of tool calls cannot be negative."
            : null;
}
