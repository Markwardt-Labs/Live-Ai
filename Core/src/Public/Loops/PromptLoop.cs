namespace Markwardt.LiveAi;

/// <summary>
/// Sends a prompt to a session over and over. The first send starts when the loop is made. After that, <see cref="Schedule" /> decides when the next send starts: as soon as the previous one is done, when it is done and an interval has passed since that one started, or never unless <see cref="Trigger" /> is called. <see cref="Trigger" /> sends at once whatever the schedule, unless a send is in progress. Sends never overlap. <see cref="PromptLoopLimits{TReply}" /> make the loop stop by itself. <see cref="IAsyncDisposable.DisposeAsync" /> stops the loop gracefully: a send in progress is allowed to finish and no other is started. <see cref="IDisposable.Dispose" /> stops it at once and cancels a send in progress. Both are safe to call more than once, and <see cref="IDisposable.Dispose" /> hurries up a graceful stop that is still waiting.
/// </summary>
/// <typeparam name="TReply">The type of a reply: <see cref="string" /> for a loop of plain prompts, or the type the AI is made to answer with.</typeparam>
public interface IPromptLoop<out TReply> : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets or sets the prompt to send: a string, or an object to write as JSON. The loop sends it to the session it was made for. A change has no effect on a send that has started; it is used by the next one.
    /// </summary>
    object Input { get; set; }

    /// <summary>
    /// Gets or sets how each send is made, with the files of the prompt among it. A change has no effect on a send that has started; it is used by the next one. For a loop of a type the options must keep the schema of the type, so change them with <c>with</c> on the current ones.
    /// </summary>
    PromptRequestOptions Options { get; set; }

    /// <summary>
    /// Gets or sets when the loop sends again: <see cref="PromptLoopSchedule.Continuous" /> (the default) as soon as the last send is done, <see cref="PromptLoopSchedule.Every(TimeSpan)" /> on an interval, or <see cref="PromptLoopSchedule.Manual" /> only when triggered. A change applies at once, also to a wait that is already going on.
    /// </summary>
    PromptLoopSchedule Schedule { get; set; }

    /// <summary>
    /// Gets the replies of the loop for watching: for each send, what <c>Request</c> returns (the text the AI wrote, or for a typed loop the answer read as <typeparamref name="TReply" />), published when the send is done, in order and never at the same time. A watcher that subscribes is first given the most recent reply, if there is one (the first send starts when the loop is made, so a watcher that arrives a moment later does not miss it), and then each new one; it does not see older ones. It ends with <c>OnCompleted</c> when the loop has stopped normally and with <c>OnError</c> and the exception when a send failed (the same exception <see cref="Task" /> faults with), and a watcher that subscribes after the end is told at once. A watcher that throws is dropped and does not disturb the loop or the others.
    /// </summary>
    IObservable<TReply> Replies { get; }

    /// <summary>
    /// Gets the task of the loop, which completes when the loop has stopped: without an error when it was disposed (after the send in progress, for a graceful disposal) or reached a limit, and faulted with the exception when a send failed (the loop stops at the first failure, so errors are never lost).
    /// </summary>
    Task Task { get; }

    /// <summary>
    /// Gets why the loop stopped, or <see cref="PromptLoopStopReason.Running" /> while it has not.
    /// </summary>
    PromptLoopStopReason StopReason { get; }

    /// <summary>
    /// Gets when the loop sends again while it is waiting for the subscription to reset (see <see cref="PromptLoopLimits{TReply}.WaitForWindow" />), or <see langword="null" /> when it is not waiting.
    /// </summary>
    DateTimeOffset? ResumesAt { get; }

    /// <summary>
    /// Sends the prompt now, without waiting for the interval. Does nothing when a send is in progress, or when the loop has stopped.
    /// </summary>
    void Trigger();
}
