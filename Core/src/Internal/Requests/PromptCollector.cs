namespace Markwardt.LiveAi;

/// <summary>
/// What a session offers besides <see cref="IPromptSession.Request(object, PromptRequestOptions?, CancellationToken)" /> to the code that needs more than the text of an answer: everything a prompt produced, which tests read events from.
/// </summary>
internal interface IPromptCollector
{
    /// <summary>
    /// Sends a prompt like <see cref="IPromptSession.Request(object, PromptRequestOptions?, CancellationToken)" /> and returns everything it produced.
    /// </summary>
    /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
    /// <param name="options">How to run it, or <see langword="null" /> for the usual.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>The result.</returns>
    Task<PromptResult> Collect(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default);
}
