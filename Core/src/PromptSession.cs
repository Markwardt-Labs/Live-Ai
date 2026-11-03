namespace Markwardt.LiveAi;

/// <summary>
/// A conversation with an AI that keeps its context from one prompt to the next, with a state (<see cref="State" />) and one output of everything it does (<see cref="Output" />), that takes input at any time (<see cref="Send" />) and answers requests (<see cref="Request" />), and that <see cref="IPromptProvider" /> can resume later from its <see cref="Id" />. Its settings (<see cref="Configuration" />) can be changed at any time and are taken when the next prompt starts. <see cref="IAsyncDisposable.DisposeAsync" /> ends the session gracefully: a prompt that is running is allowed to finish, input still waiting is dropped, and then the AI's process is asked to exit (its input is closed and it gets a few seconds before it is ended) and the tool server is stopped. <see cref="IDisposable.Dispose" /> ends it at once: the prompt that is running is cancelled and the input waiting is dropped, the process is ended without waiting, and the rest is cleaned up in the background. Calling <see cref="IDisposable.Dispose" /> while a graceful disposal is waiting hurries it up the same way. Both are safe to call more than once, and prompts sent afterwards throw <see cref="ObjectDisposedException" />.
/// </summary>
public interface IPromptSession : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the identifier the library gave the session (a GUID in text form, without hyphens), which <see cref="IPromptProvider" /> resumes it from.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the provider that made or loaded the session, whose tool the session runs on.
    /// </summary>
    IPromptProvider Provider { get; }

    /// <summary>
    /// Gets what the session has used since this object was made, added up from the usage the tool reported for each prompt. A session loaded in another process starts at zero, and so does a branch.
    /// </summary>
    PromptUsage Usage { get; }

    /// <summary>
    /// Gets what the session is doing: <see cref="PromptSessionState.Waiting" /> for input, <see cref="PromptSessionState.Working" /> on input it was given, or <see cref="PromptSessionState.Responding" /> to a direct request. Input can be given in the first two states and throws in the last.
    /// </summary>
    PromptSessionState State { get; }

    /// <summary>
    /// Gets everything the AI outputs as it happens, from the main conversation: its messages and their deltas, tool calls and results, usage, subscription windows, interruptions and failures, together with a <see cref="PromptStateEvent" /> whenever <see cref="State" /> changes. It is a hot sequence: a watcher gets what happens after it subscribes and nothing is replayed (<see cref="State" /> is the current state and <see cref="GetHistory" /> reads what was said before), and it completes when the session is disposed. Work that fails is reported as a <see cref="PromptFailureEvent" /> and does not end it. Work done in a fork (<see cref="PromptRequestMode.Fork" />) is not part of it. Watchers are called as the events happen, so they should not block.
    /// </summary>
    IObservable<PromptEvent> Output { get; }

    /// <summary>
    /// Gets the settings of the session: the model and its settings, whether its process stays alive, the shell, instructions, the tools and stores it can use, and what every request starts with. Assign to them whenever you like; they are taken together when the next prompt starts (see <see cref="IPromptSessionConfiguration" />).
    /// </summary>
    IPromptSessionConfiguration Configuration { get; }

    /// <summary>
    /// Sends a prompt, waits for the AI to finish it and returns its answer. A string is the prompt text, and any other object is written as JSON with its own runtime type (it should then carry its own instruction, a <c>Task</c> property for example). Where and when it runs depends on <see cref="PromptRequestOptions.Mode" />: by default it needs the session to be waiting and throws a <see cref="PromptSessionStateException" /> otherwise. Prompts asked of a session wait until it is free, because a provider's session cannot take two at once. What the AI does while it works (its messages, tool calls, usage) is on <see cref="Output" /> for a request in the main conversation. The extension methods <c>Request&lt;T&gt;</c>, which reads the answer into a type, and <c>Loop</c> are built on this.
    /// </summary>
    /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
    /// <param name="options">How to run it, or <see langword="null" /> for the session's defaults.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>Without a schema (<see cref="PromptRequestOptions.Schema" />) everything the AI wrote, one message after another; with one the answer alone, the last message, as JSON text.</returns>
    /// <exception cref="ArgumentException">The schema is not one whose root is an object, there are too many or too large files, a text file is too large or not valid UTF-8, or a tool or a mount added to the request has a name that is not allowed or is taken.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value of <paramref name="options" /> is not allowed (a timeout that is not positive or is longer than 24 days, a retry that is not valid, a limit on tool calls that is negative, or an undefined enum value).</exception>
    /// <exception cref="NotSupportedException">A file is of a type that the provider cannot be shown and that is not text.</exception>
    /// <exception cref="PromptLimitException">The timeout, the window limit or the limit on tool calls was reached.</exception>
    /// <exception cref="PromptSessionException">The tool could not be started or the AI failed.</exception>
    /// <exception cref="PromptSessionStateException">The mode needs a state the session is not in.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task<string> Request(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Gives the AI input without waiting for an answer, at any time except while the session is responding to a direct request. What the AI does with it shows on <see cref="Output" />. The input reaches the AI immediately and is taken into the work it is doing, when its engine can take input in the middle of work (the Claude and Codex engines can); otherwise it waits and the AI gets it when the work ends. To get an answer back for a particular input use <see cref="Request" />, in one of the modes of <see cref="PromptRequestMode" />.
    /// </summary>
    /// <param name="input">The input: a string, or an object to write as JSON.</param>
    /// <param name="options">The files and the note that go with the input, or <see langword="null" /> for none. A <see cref="PromptRequestOptions" /> is accepted, but only the attachments and the note of it are used.</param>
    /// <param name="cancellation">Cancels the delivery.</param>
    /// <returns>A task that completes when the input was written to the running AI process, or was queued for the next work, which is before the AI acts on it.</returns>
    /// <exception cref="PromptSessionStateException">The session is responding to a direct request.</exception>
    /// <exception cref="ArgumentException">The note is blank, there are too many or too large files, a text file is too large or not valid UTF-8, or the schema of a file is not allowed.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task Send(object input, PromptOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Stops the prompt that is running without cancelling it: the AI stops where it is, the prompt ends normally with what was written until then and a <see cref="PromptInterruptedEvent" />, and the conversation goes on with the next prompt, so the AI can be corrected. A kept Claude Code process is asked to stop and stays alive; otherwise the process is ended, and the next prompt resumes the conversation as the tool stored it (a first prompt that is interrupted before the tool has reported its session leaves nothing to resume). To change course without stopping the AI, give it more input with <see cref="Send" />.
    /// </summary>
    /// <param name="cancellation">Cancels the request to stop.</param>
    /// <returns><see langword="true" /> if a prompt was running, and <see langword="false" /> if the session was idle.</returns>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task<bool> Interrupt(CancellationToken cancellation = default);

    /// <summary>
    /// Branches the conversation: makes a new session, with an identifier of its own, that starts with the conversation of this one and then goes its own way, so two approaches can be tried from the same point. It waits for the prompt that is running, if any, so the branch starts after it. The branch has the configuration this session has now (including the tools, the mounts and the shell approval, which are shared with it) and the same API key, but a scratch folder of its own. A session that has had no prompt yet has nothing to branch from, and the branch is simply a new session with the same configuration. This session is not changed.
    /// </summary>
    /// <param name="at">An entry of the history, as <see cref="GetHistory" /> returned it, to branch at the end of the exchange it belongs to (a prompt and everything the AI did to answer it, the finest point both providers branch at), or <see langword="null" /> for the whole conversation so far.</param>
    /// <param name="cancellation">Cancels the branching.</param>
    /// <returns>The new session, which the caller disposes.</returns>
    /// <exception cref="PromptSessionException">The provider's conversation could not be found or branched, or has no exchange at <paramref name="at" />.</exception>
    /// <exception cref="NotSupportedException"><paramref name="at" /> is given and the provider cannot branch at a point.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task<IPromptSession> Fork(PromptHistoryPoint? at = null, CancellationToken cancellation = default);

    /// <summary>
    /// Takes the conversation back to the end of the exchange that an entry of its history belongs to, so that what came after is no longer part of it and the next prompt continues from there. The session keeps its identifier. The provider's own record of the conversation is not changed: the session continues in a branch of it, and the later exchanges stay in the original, which the tool still lists, until the session is deleted or the tool's own storage is cleaned. It waits for the prompt that is running.
    /// </summary>
    /// <param name="to">An entry of the history, as <see cref="GetHistory" /> returned it.</param>
    /// <param name="cancellation">Cancels the rewind.</param>
    /// <returns>A task that completes once the session continues from the entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="to" /> is <see langword="null" />.</exception>
    /// <exception cref="PromptSessionException">The session has no conversation, or the provider could not branch it or has no exchange at that entry.</exception>
    /// <exception cref="NotSupportedException">The provider cannot branch at a point.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task Rewind(PromptHistoryPoint to, CancellationToken cancellation = default);

    /// <summary>
    /// Moves the session to another provider session, another workspace folder, or both. The session keeps its identifier and its other settings; what it was attached to before is left alone: the conversation stays in the tool's storage and the old workspace folder is not touched. It waits for the prompt that is running and ends a kept process.
    /// </summary>
    /// <param name="aiSessionId">The identifier of the provider's own session to continue, as its tool reports it (the session identifier of Claude Code, the thread identifier of Codex), or <see langword="null" /> for a new conversation that starts with the next prompt.</param>
    /// <param name="options">The workspace to move to (<see cref="PromptSessionOptions.WorkspacePath" /> and <see cref="PromptSessionOptions.Sandbox" />): the folder on disk that becomes the session's workspace, which <see cref="IPromptSessionConfiguration.Workspace" /> is set to and the tool runs in, or no path for no workspace, which sets the workspace to nothing and runs the tool in an empty folder of the session's own. The session keeps its API key, so giving one is refused.</param>
    /// <param name="cancellation">Cancels the move.</param>
    /// <returns>A task that completes once the session is attached.</returns>
    /// <exception cref="ArgumentException"><paramref name="aiSessionId" /> is empty or blank, or an API key is given.</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace path is not an existing folder.</exception>
    /// <exception cref="PromptSessionNotFoundException">The provider has no session with that identifier.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task AttachTo(string? aiSessionId = null, PromptSessionOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Makes the tool shorten the conversation to a summary, which frees room in the context window of a long one. It waits for the prompt that is running, and a persistent process is used when there is one. The compacted conversation is the one the session continues with.
    /// </summary>
    /// <param name="cancellation">Cancels the compaction.</param>
    /// <returns>A task that completes once the conversation is compacted.</returns>
    /// <exception cref="PromptSessionException">The tool could not compact the conversation (it is too short, for one).</exception>
    /// <exception cref="NotSupportedException">The provider cannot compact a conversation.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task Compact(CancellationToken cancellation = default);

    /// <summary>
    /// Reads how much of the context window of the model the conversation fills now, which the tool's own instructions and tool descriptions count in, so that a caller can compact before a long conversation overflows. It waits for the prompt that is running. Before the first prompt of a Codex conversation it reports no tokens and no size.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The size of the conversation and of the window.</returns>
    /// <exception cref="PromptSessionException">The tool did not report it.</exception>
    /// <exception cref="NotSupportedException">The provider cannot report it.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task<PromptContext> GetContext(CancellationToken cancellation = default);

    /// <summary>
    /// Reads the conversation of the session as the provider stored it, newest entry first and from the end of the stored file, so taking only the first few entries of a long conversation reads little of it: the prompts, what the AI wrote, and its tool calls and their results. Prompts that were folded with their text files or shown with images appear as the provider stored them. It is empty for a session that has had no prompt, and it also includes what is stored for the point a branch was made from. Cut it with the usual operators (<c>Take</c>, <c>SkipWhile</c>) or with <see cref="PromptHistoryExtensions.OlderThan" /> and <see cref="PromptHistoryExtensions.NewerThan" />, which page back through a long conversation or watch it grow from a <see cref="PromptHistoryPoint" /> that was seen before.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The entries, newest first, read as they are enumerated. Enumerating throws <see cref="PromptSessionException" /> when the provider's conversation is stored but cannot be read.</returns>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    IAsyncEnumerable<PromptHistoryEntry> GetHistory(CancellationToken cancellation = default);
}

/// <summary>
/// Conveniences for <see cref="IPromptSession" /> built on <see cref="IPromptSession.Request(object, PromptRequestOptions?, CancellationToken)" />: an answer read into a type, loops, and forking and rewinding at an entry instead of a point.
/// </summary>
public static class PromptSessionExtensions
{
    extension(IPromptSession session)
    {
        /// <summary>
        /// Sends a prompt and waits for the AI's structured answer, a <typeparamref name="TOutput" /> that the AI is made to write as JSON. Any schema in <paramref name="options" /> is replaced by the one of the type.
        /// </summary>
        /// <typeparam name="TOutput">The type of the answer. A type that is not written as a JSON object (a string, a number, a list, an enumeration) is asked for in an object with one property and unwrapped again, because the AI tools require an object.</typeparam>
        /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
        /// <param name="options">How to run it, or <see langword="null" /> for the session's defaults.</param>
        /// <param name="cancellation">Ends the work in progress.</param>
        /// <returns>The answer.</returns>
        /// <exception cref="PromptSessionOutputException">The AI wrote no answer, or one that is not a <typeparamref name="TOutput" />.</exception>
        /// <exception cref="PromptSessionException">The tool could not be started or the AI failed.</exception>
        [RequiresUnreferencedCode("The type is read and written by reflection, which trimming can remove members of. Keep the members of the type, or use the plain overload with your own schema.")]
        [RequiresDynamicCode("The type is turned into a JSON schema and read by runtime code generation, which native AOT does not support.")]
        public async Task<TOutput> Request<TOutput>(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
        {
            (JsonElement schema, bool wrapped) = PromptJson.Output(typeof(TOutput));
            string answer = await session.Request(input, (options ?? new()) with { Schema = schema }, cancellation);
            return PromptJson.Deserialize<TOutput>(answer.Length > 0 ? [answer] : [], wrapped);
        }

        /// <summary>
        /// Branches the conversation at the end of the exchange an entry of its history belongs to, like <see cref="IPromptSession.Fork(PromptHistoryPoint?, CancellationToken)" /> at the point of the entry.
        /// </summary>
        /// <param name="at">An entry of the history, as <see cref="IPromptSession.GetHistory" /> returned it.</param>
        /// <param name="cancellation">Cancels the branching.</param>
        /// <returns>The new session, which the caller disposes.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="at" /> is <see langword="null" />.</exception>
        public Task<IPromptSession> Fork(PromptHistoryEntry at, CancellationToken cancellation = default)
            => session.Fork((at ?? throw new ArgumentNullException(nameof(at))).Point, cancellation);

        /// <summary>
        /// Takes the conversation back to the end of the exchange an entry of its history belongs to, like <see cref="IPromptSession.Rewind" /> to the point of the entry.
        /// </summary>
        /// <param name="to">An entry of the history, as <see cref="IPromptSession.GetHistory" /> returned it.</param>
        /// <param name="cancellation">Cancels the rewind.</param>
        /// <returns>A task that completes when the session continues from there.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="to" /> is <see langword="null" />.</exception>
        public Task Rewind(PromptHistoryEntry to, CancellationToken cancellation = default)
            => session.Rewind((to ?? throw new ArgumentNullException(nameof(to))).Point, cancellation);

        /// <summary>
        /// Starts sending a prompt over and over: once now, and then again as the schedule says (see <see cref="PromptLoopSchedule" />), or when <see cref="IPromptLoop{TReply}.Trigger" /> is called. Each reply is the text the AI wrote.
        /// </summary>
        /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
        /// <param name="schedule">When the prompt is sent again (<see cref="PromptLoopSchedule.Every(TimeSpan)" /> for an interval), or <see langword="null" /> for as soon as each send is done (<see cref="PromptLoopSchedule.Continuous" />).</param>
        /// <param name="options">How to run each send, or <see langword="null" /> for the session's defaults.</param>
        /// <param name="limits">When the loop stops by itself, or <see langword="null" /> for never.</param>
        /// <returns>The loop, which the caller disposes to stop it.</returns>
        public IPromptLoop<string> Loop(object input, PromptLoopSchedule? schedule = null, PromptRequestOptions? options = null, PromptLoopLimits<string>? limits = null)
            => new PromptLoop<string>(session, input, schedule ?? PromptLoopSchedule.Continuous, options, text => text, limits, null, (session as IPromptCollector)?.Time);

        /// <summary>
        /// Starts a loop like the plain loop whose replies are structured: the AI is made to answer in the shape of <typeparamref name="TOutput" /> every time, and each answer is read into one.
        /// </summary>
        /// <typeparam name="TOutput">The type of the answer. A type that is not written as a JSON object (a string, a number, a list, an enumeration) is asked for in an object with one property and unwrapped again, because the AI tools require an object.</typeparam>
        /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
        /// <param name="schedule">When the prompt is sent again (<see cref="PromptLoopSchedule.Every(TimeSpan)" /> for an interval), or <see langword="null" /> for as soon as each send is done (<see cref="PromptLoopSchedule.Continuous" />).</param>
        /// <param name="options">How to run each send, or <see langword="null" /> for the session's defaults.</param>
        /// <param name="limits">When the loop stops by itself, or <see langword="null" /> for never.</param>
        /// <returns>The loop, which the caller disposes to stop it. When an answer is not a <typeparamref name="TOutput" /> the loop fails with a <see cref="PromptSessionOutputException" />.</returns>
        [RequiresUnreferencedCode("The type is read and written by reflection, which trimming can remove members of. Keep the members of the type, or use the plain overload with your own schema.")]
        [RequiresDynamicCode("The type is turned into a JSON schema and read by runtime code generation, which native AOT does not support.")]
        public IPromptLoop<TOutput> Loop<TOutput>(object input, PromptLoopSchedule? schedule = null, PromptRequestOptions? options = null, PromptLoopLimits<TOutput>? limits = null)
        {
            (JsonElement schema, bool wrapped) = PromptJson.Output(typeof(TOutput));
            return new PromptLoop<TOutput>(session, input, schedule ?? PromptLoopSchedule.Continuous, (options ?? new()) with { Schema = schema }, text => PromptJson.Deserialize<TOutput>(text.Length > 0 ? [text] : [], wrapped), limits, null, (session as IPromptCollector)?.Time);
        }
    }
}
