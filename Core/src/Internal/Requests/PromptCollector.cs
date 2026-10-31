namespace Markwardt.LiveAi;

/// <summary>
/// What a session offers besides <see cref="IPromptSession.Request(object, PromptRequestOptions?, CancellationToken)" /> to the parts of the library that need more than the text of an answer: a loop, which adds up the usage of its sends.
/// </summary>
internal interface IPromptCollector
{
    /// <summary>
    /// Gets the clock the session's timeouts, waits and schedules run on.
    /// </summary>
    TimeProvider Time { get; }

    /// <summary>
    /// Sends a prompt like <see cref="IPromptSession.Request(object, PromptRequestOptions?, CancellationToken)" /> and returns everything it produced.
    /// </summary>
    /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
    /// <param name="options">How to run it, or <see langword="null" /> for the usual.</param>
    /// <param name="limits">A window limit to run under besides the session's, or <see langword="null" /> for none.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>The result.</returns>
    Task<PromptResult> Collect(object input, PromptRequestOptions? options = null, PromptRequestLimits? limits = null, CancellationToken cancellation = default);
}
