namespace Markwardt.LiveAi;

/// <summary>
/// What runs a prompt for a <see cref="IPromptRequester" />. It is not part of the public API, which is <see cref="IPromptSession.Request" />.
/// </summary>
internal interface IPromptExecutor
{
    /// <summary>
    /// Runs a prompt and reports what happens while the AI works. Prompts run one at a time, in the order they arrive.
    /// </summary>
    /// <param name="run">The prompt and how to run it.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>The events as they happen; the sequence ends when the AI has finished.</returns>
    IAsyncEnumerable<PromptEvent> Execute(PromptRun run, CancellationToken cancellation = default);
}
