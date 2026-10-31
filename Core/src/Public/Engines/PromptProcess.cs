namespace Markwardt.LiveAi;

/// <summary>
/// A running process of the AI's tool that holds a conversation and takes any number of prompts, which an engine hands to the library when it attaches to a process it started.
/// </summary>
public interface IPromptProcess : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether the process is still running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Runs one prompt and reports what happens until the AI has answered it. When the enumeration is stopped or cancelled before that, the process is ended, since it cannot take another prompt in the middle of one.
    /// </summary>
    /// <param name="input">The prompt.</param>
    /// <param name="cancellation">Cancels the prompt.</param>
    /// <returns>The events in order; a <see cref="PromptEngineEventKind.Failed" /> event is added when the process ended without reporting why.</returns>
    IAsyncEnumerable<PromptEngineEvent> Run(PromptInput input, CancellationToken cancellation = default);

    /// <summary>
    /// Gives input to the AI while it works on a prompt that <see cref="Run" /> is reporting, so that it takes it into the work it is doing, as a person's message sent to a colleague who is busy. The reply to it is part of the events of that <see cref="Run" />, which does not end before the AI has dealt with the input.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns><see langword="true" /> if the AI took the input into the work it is doing, and <see langword="false" /> if nothing is running that it could join, in which case the caller gives it as a prompt of its own.</returns>
    Task<bool> Steer(PromptInput input, CancellationToken cancellation = default);

    /// <summary>
    /// Makes the tool shorten the conversation to a summary, which frees room in the context window.
    /// </summary>
    /// <param name="cancellation">Cancels the compaction.</param>
    /// <returns>The events in order, which tell the engine session the compacted conversation continues in.</returns>
    IAsyncEnumerable<PromptEngineEvent> Compact(CancellationToken cancellation = default);

    /// <summary>
    /// Reads how much of the context window the conversation fills.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The size of the conversation and of the window.</returns>
    /// <exception cref="PromptSessionException">The tool did not report it.</exception>
    Task<PromptContext> GetContext(CancellationToken cancellation = default);

    /// <summary>
    /// Makes the tool stop what it is doing, keeping the conversation.
    /// </summary>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>A task that completes once the request is written.</returns>
    /// <exception cref="NotSupportedException">The tool cannot be interrupted without ending its process.</exception>
    Task Interrupt(CancellationToken cancellation = default);

    /// <summary>
    /// Ends the process at once, with everything it started.
    /// </summary>
    void Stop();
}
