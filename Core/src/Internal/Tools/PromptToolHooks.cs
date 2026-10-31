namespace Markwardt.LiveAi;

/// <summary>
/// What is told of the calls of the tools the library offers, which a session turns into the hooks in its configuration.
/// </summary>
internal interface IPromptToolHooks
{
    /// <summary>
    /// Decides whether a call may run.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellation">Cancels the decision.</param>
    /// <returns>The decision; when this throws the call is refused.</returns>
    Task<PromptToolDecision> Before(PromptToolCallEvent call, CancellationToken cancellation = default);

    /// <summary>
    /// Reports a call that has run.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="result">What became of it.</param>
    /// <param name="duration">How long the tool took.</param>
    /// <param name="cancellation">Cancels the report.</param>
    /// <returns>A task that completes once the report is taken.</returns>
    Task After(PromptToolCallEvent call, PromptToolResultEvent result, TimeSpan duration, CancellationToken cancellation = default);
}
