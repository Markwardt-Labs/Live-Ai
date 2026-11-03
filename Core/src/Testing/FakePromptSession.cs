namespace Markwardt.LiveAi.Testing;

/// <summary>
/// An <see cref="IPromptSession" /> for tests that answers from a script instead of running a command line tool. Prompts go through the real request pipeline, so everything built on <see cref="IPromptSession.Request" /> works as it does in production: files, structured answers, options, timeouts, retries, limits, loops and events. Script the answers with <see cref="Reply(object)" />, <see cref="Fail(Exception)" />, <see cref="Hang" /> and <see cref="ReplyWith" />, and look at what was asked in <see cref="Prompts" />. Nothing is stored and no process, port or file is made.
/// </summary>
public sealed class FakePromptSession : IPromptSession, IPromptCollector
{
    private readonly object gate = new();

    private readonly SemaphoreSlim turn = new(1, 1);

    private readonly CancellationTokenSource closing = new();

    private readonly Queue<Script> scripts = new();

    private readonly List<FakePrompt> prompts = [];

    private readonly List<Exchange> exchanges = [];

    private readonly List<string> notes = [];

    private readonly Func<IReadOnlyList<PromptWindow>?>? sharedWindows;

    private readonly IPromptRequester requester;

    private readonly TimeProvider time;

    private readonly Broadcast<PromptEvent> output = new(false);

    private readonly List<Queued> queued = [];

    private readonly List<Queued> delivered = [];

    private Func<FakePrompt, IEnumerable<PromptEvent>>? responder;

    private readonly PromptSessionSettings settings;

    private PromptUsage usage = new();

    private CancellationTokenSource? turnInterrupt;

    private bool turnInterrupted;

    private PromptContext context = new() { Tokens = 0, Limit = 0 };

    private int compactions;

    private string? attachedTo;

    private IReadOnlyList<PromptWindow>? windows;

    private FakePrompt? workingOn;

    private bool disposed;

    private PromptSessionState state = PromptSessionState.Waiting;

    private Task pumping = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakePromptSession" /> class.
    /// </summary>
    /// <param name="provider">The provider the session says it runs on, Claude Code by default. The session starts with the first model of a <see cref="FakePromptProvider" />, and with the model of a fake one when the provider is not.</param>
    /// <param name="apiKey">The API key the session was made with, which is only kept for a test to look at.</param>
    /// <param name="time">The clock the session's timeouts, retries and loops run on, or <see langword="null" /> for the system's. Give a <see cref="TimeProvider" /> that the test controls to test code that waits without waiting.</param>
    public FakePromptSession(IPromptProvider? provider = null, string? apiKey = null, TimeProvider? time = null)
        : this(provider ?? new PromptProvider(new ClaudeEngine()), apiKey, null, time, (provider as FakePromptProvider)?.Models.FirstOrDefault())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakePromptSession" /> class.
    /// </summary>
    /// <param name="provider">The provider the session reports.</param>
    /// <param name="apiKey">The API key the session reports.</param>
    /// <param name="sharedWindows">Gives the windows of the provider the session belongs to, or <see langword="null" /> for none.</param>
    /// <param name="time">The clock the session runs on, or <see langword="null" /> for the system's.</param>
    /// <param name="model">The model the session starts with, or <see langword="null" /> for a fake one.</param>
    internal FakePromptSession(IPromptProvider provider, string? apiKey, Func<IReadOnlyList<PromptWindow>?>? sharedWindows, TimeProvider? time = null, PromptModel? model = null)
    {
        this.time = time ?? TimeProvider.System;
        Provider = provider;
        ApiKey = apiKey;
        this.sharedWindows = sharedWindows;
        requester = new PromptRequester(new Executor(this), null, this.time);
        Id = Guid.NewGuid().ToString("N");
        PromptLibraryTools library = new();
        PromptModel start = model ?? new FakePromptProvider().Models[0];
        settings = new PromptSessionSettings(new PromptSessionRecord { Id = Guid.NewGuid(), Engine = new ClaudeEngine(), Model = start, ModelSettings = start.Reconcile(new Dictionary<string, string>()).Settings, WorkingFolder = string.Empty }, [.. library.Names, library.ShellName, library.AskName, "respond"]);
    }

    /// <inheritdoc />
    public string Id { get; private set; }

    /// <inheritdoc />
    public IPromptProvider Provider { get; }

    /// <inheritdoc />
    public PromptUsage Usage
    {
        get
        {
            lock (gate)
            {
                return usage;
            }
        }
    }

    /// <inheritdoc />
    public IPromptSessionConfiguration Configuration => settings;

    /// <inheritdoc />
    public PromptSessionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <inheritdoc />
    public IObservable<PromptEvent> Output => output;

    /// <summary>
    /// Gets the API key the session was made with, or <see langword="null" /> for none.
    /// </summary>
    public string? ApiKey { get; }

    /// <summary>
    /// Gets or sets the limit windows that a window limit reads, which is the one of the provider that made the session until a test sets one for this session. With none, reading the status fails the way a login without limits does.
    /// </summary>
    public IReadOnlyList<PromptWindow>? Windows
    {
        get
        {
            lock (gate)
            {
                return windows ?? sharedWindows?.Invoke();
            }
        }

        set
        {
            lock (gate)
            {
                windows = value;
            }
        }
    }

    /// <summary>
    /// Gets the identifier of the provider session the session was last attached to, or <see langword="null" /> if it never was or was attached to a new conversation.
    /// </summary>
    public string? AttachedTo
    {
        get
        {
            lock (gate)
            {
                return attachedTo;
            }
        }
    }

    /// <summary>
    /// Gets how many times the session was compacted.
    /// </summary>
    public int Compactions
    {
        get
        {
            lock (gate)
            {
                return compactions;
            }
        }
    }

    /// <summary>
    /// Gets or sets what <see cref="GetContext(CancellationToken)" /> reports, which is no tokens in a window of no size until a test sets it.
    /// </summary>
    public PromptContext Context
    {
        get
        {
            lock (gate)
            {
                return context;
            }
        }

        set
        {
            lock (gate)
            {
                context = value;
            }
        }
    }

    /// <summary>
    /// Gets the prompts the session started, in order, as the real session would have run them.
    /// </summary>
    public IReadOnlyList<FakePrompt> Prompts
    {
        get
        {
            lock (gate)
            {
                return [.. prompts];
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the session was disposed.
    /// </summary>
    public bool IsDisposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
        }
    }

    /// <summary>
    /// Scripts the answer to the next prompt that has none scripted yet: a string is one message, an <see cref="PromptEvent" /> is that event, and any other object is written as JSON, which is what a structured answer is.
    /// </summary>
    /// <param name="reply">The answer.</param>
    /// <returns>This session, for chaining.</returns>
    public FakePromptSession Reply(object reply)
        => Reply(reply switch
        {
            PromptEvent output => output,
            string text => new PromptMessageEvent { Text = text },
            _ => new PromptMessageEvent { Text = PromptJson.Serialize(reply) }
        });

    /// <summary>
    /// Scripts the events of the next prompt that has none scripted yet, in order: messages, tool calls, usage and so on.
    /// </summary>
    /// <param name="events">The events.</param>
    /// <returns>This session, for chaining.</returns>
    public FakePromptSession Reply(params PromptEvent[] events)
        => Enqueue((_, _) => events.ToAsyncEnumerable());

    /// <summary>
    /// Scripts a failure for the next prompt that has none scripted yet, thrown before it reports anything, which is what a retry can recover from.
    /// </summary>
    /// <param name="failure">The error.</param>
    /// <returns>This session, for chaining.</returns>
    public FakePromptSession Fail(Exception failure)
        => Enqueue((_, _) => Failing(failure));

    /// <summary>
    /// Scripts a <see cref="PromptSessionException" /> for the next prompt that has none scripted yet.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <returns>This session, for chaining.</returns>
    public FakePromptSession Fail(string message)
        => Fail(new PromptSessionException(message));

    /// <summary>
    /// Scripts a prompt that never finishes by itself, to try timeouts, cancellation, limits and interruptions: it ends when it is cancelled, interrupted or the session is disposed.
    /// </summary>
    /// <returns>This session, for chaining.</returns>
    public FakePromptSession Hang()
        => Enqueue((_, cancellation) => Hanging(cancellation));

    /// <summary>
    /// Sets how prompts are answered once the scripted answers are used up, from the prompt. Without it, a prompt that has no scripted answer fails with an <see cref="InvalidOperationException" /> that shows the prompt, so a test cannot pass by accident.
    /// </summary>
    /// <param name="answer">Gives the events for a prompt.</param>
    /// <returns>This session, for chaining.</returns>
    public FakePromptSession ReplyWith(Func<FakePrompt, IEnumerable<PromptEvent>> answer)
    {
        lock (gate)
        {
            responder = answer;
        }

        return this;
    }

    /// <inheritdoc />
    public async Task<string> Request(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        RequireModel();
        return (await ((IPromptCollector)this).Collect(input, options, null, cancellation)).Text;
    }

    TimeProvider IPromptCollector.Time => time;

    Task<PromptResult> IPromptCollector.Collect(object input, PromptRequestOptions? options, PromptRequestLimits? limits, CancellationToken cancellation)
        => requester.Collect(input, options ?? new(), settings.RequestLimits(limits), cancellation);

    /// <inheritdoc />
    public Task Send(object input, PromptOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireModel();

        foreach (string note in options?.Notes ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(note);
        }

        Deliver(new Queued(input is string text ? text : PromptJson.Serialize(input), options?.Attachments ?? [], null, null, options?.Notes ?? []));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> Interrupt(CancellationToken cancellation = default)
    {
        CancellationTokenSource? source;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            source = turnInterrupt;

            if (source is null)
            {
                return false;
            }

            turnInterrupted = true;
        }

        try
        {
            await source.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public Task<IPromptSession> Fork(PromptHistoryPoint? at = null, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            FakePromptSession fork = Copy(new FakePromptSession(Provider, ApiKey, sharedWindows, time));

            if (at is { } point)
            {
                fork.Truncate(TurnsThrough(point));
            }

            return Task.FromResult<IPromptSession>(fork);
        }
    }

    /// <inheritdoc />
    public async Task Rewind(PromptHistoryPoint to, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await turn.WaitAsync(cancellation);

        try
        {
            lock (gate)
            {
                Truncate(TurnsThrough(to));
            }
        }
        finally
        {
            turn.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>The conversation the fake recorded is dropped, since the session is now in another one. The provider session is not looked up.</remarks>
    public async Task AttachTo(string? aiSessionId = null, PromptSessionOptions? options = null, CancellationToken cancellation = default)
    {
        if (options?.ApiKey is not null)
        {
            throw new ArgumentException("A session keeps its API key; it cannot be changed by attaching it elsewhere.", nameof(options));
        }

        string? workspacePath = options?.WorkspacePath;
        bool sandbox = options?.Sandbox ?? true;
        if (aiSessionId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(aiSessionId);
        }

        ObjectDisposedException.ThrowIf(IsDisposed, this);
        IStore? workspace = workspacePath is null ? null : IStore.FromDisk(workspacePath, sandbox);
        await turn.WaitAsync(cancellation);

        try
        {
            lock (gate)
            {
                attachedTo = aiSessionId;
                Truncate(0);
            }

            Configuration.Workspace = workspace;
        }
        finally
        {
            turn.Release();
        }
    }

    /// <inheritdoc />
    public async Task Compact(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        RequireModel();
        await turn.WaitAsync(cancellation);

        try
        {
            lock (gate)
            {
                compactions++;
            }
        }
        finally
        {
            turn.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PromptContext> GetContext(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        RequireModel();
        await turn.WaitAsync(cancellation);

        try
        {
            return Context;
        }
        finally
        {
            turn.Release();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<PromptHistoryEntry> GetHistory(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        List<PromptHistoryEntry> oldestFirst = [];
        long position = 0;

        lock (gate)
        {
            foreach (Exchange exchange in exchanges)
            {
                oldestFirst.Add(new PromptHistoryEntry { Event = new PromptInputEvent { Text = exchange.Prompt.Text }, Time = exchange.Prompt.Time, Point = new PromptHistoryPoint(position++) });

                foreach (PromptEvent output in exchange.Events)
                {
                    PromptHistoryEntry? entry = output is PromptMessageEvent or PromptToolCallEvent or PromptToolResultEvent ? new PromptHistoryEntry { Event = output, Time = exchange.Prompt.Time, Point = new PromptHistoryPoint(position) } : null;

                    if (entry is not null)
                    {
                        oldestFirst.Add(entry);
                        position++;
                    }
                }
            }
        }

        oldestFirst.Reverse();
        return oldestFirst.ToAsyncEnumerable();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Close();
        closing.Cancel();
        output.Complete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Close();
        await turn.WaitAsync();
        turn.Release();
        await pumping.ContinueWith(static _ => { }, CancellationToken.None);
        output.Complete();
    }

    internal FakePromptSession Resume()
    {
        lock (gate)
        {
            FakePromptSession resumed = new(Provider, ApiKey, sharedWindows, time) { Id = Id };
            return Copy(resumed);
        }
    }

    private FakePromptSession Copy(FakePromptSession copy)
    {
        PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
        copy.responder = responder;
        snapshot.CopyTo(copy.settings);

        foreach (Script script in scripts)
        {
            copy.scripts.Enqueue(script);
        }

        copy.prompts.AddRange(prompts);
        copy.exchanges.AddRange(exchanges);
        return copy;
    }

    private int TurnsThrough(PromptHistoryPoint point)
    {
        long position = 0;

        for (int index = 0; index < exchanges.Count; index++)
        {
            position += 1 + exchanges[index].Events.Count(output => output is PromptMessageEvent or PromptToolCallEvent or PromptToolResultEvent);

            if (point.Position < position)
            {
                return index + 1;
            }
        }

        throw new PromptSessionException("The conversation has no exchange at that entry.");
    }

    private void Truncate(int turns)
    {
        lock (gate)
        {
            if (turns < exchanges.Count)
            {
                exchanges.RemoveRange(turns, exchanges.Count - turns);
                prompts.RemoveRange(turns, prompts.Count - turns);
            }
        }
    }

    private FakePromptSession Enqueue(Script script)
    {
        lock (gate)
        {
            scripts.Enqueue(script);
        }

        return this;
    }

    private void Close()
    {
        lock (gate)
        {
            disposed = true;
        }

        settings.Close();
    }

    private async IAsyncEnumerable<PromptEvent> Failing(Exception failure)
    {
        await Task.Yield();
        throw failure;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async IAsyncEnumerable<PromptEvent> Hanging([EnumeratorCancellation] CancellationToken cancellation)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        yield break;
    }

    private delegate IAsyncEnumerable<PromptEvent> Script(FakePrompt prompt, CancellationToken cancellation);

    private sealed record Exchange(FakePrompt Prompt, IReadOnlyList<PromptEvent> Events);

    private sealed record Queued(string Text, IReadOnlyList<PromptAttachment> Attachments, JsonElement? Schema, TaskCompletionSource<string>? Completion, IReadOnlyList<string> Notes);

    private sealed class Executor(FakePromptSession session) : IPromptExecutor
    {
        public Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            return session.Windows is { } windows ? Task.FromResult(windows) : Task.FromException<IReadOnlyList<PromptWindow>>(new PromptSessionException("The fake provider reported no subscription limits."));
        }

        public IAsyncEnumerable<PromptEvent> Execute(PromptRun run, CancellationToken cancellation = default)
        {
            session.RequireModel();

            return run.Mode switch
            {
                PromptRequestMode.Direct => session.Direct(run, cancellation),
                PromptRequestMode.Mixed => session.Mixed(run, cancellation),
                PromptRequestMode.Fork => session.Forked(run, cancellation),
                _ => throw new ArgumentOutOfRangeException(nameof(run))
            };
        }
    }

    /// <summary>
    /// Runs the request as the main conversation's response, with the session responding to it meanwhile.
    /// </summary>
    /// <param name="run">The request.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The events of the prompt.</returns>
    /// <exception cref="PromptSessionStateException">The session is not waiting.</exception>
    private async IAsyncEnumerable<PromptEvent> Direct(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (state != PromptSessionState.Waiting)
            {
                throw new PromptSessionStateException(state, $"A request can only be asked directly while the session is waiting, and it is {state.ToString().ToLowerInvariant()}. Wait for it, or ask in another mode.");
            }

            notes.AddRange(run.Notes);

            Enter(PromptSessionState.Responding);
        }

        try
        {
            await using IAsyncEnumerator<PromptEvent> events = Turn(run, cancellation).GetAsyncEnumerator(cancellation);

            while (true)
            {
                try
                {
                    if (!await events.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or ObjectDisposedException))
                {
                    output.Publish(new PromptFailureEvent { Message = exception.Message, Exception = exception });
                    throw;
                }

                output.Publish(events.Current);
                yield return events.Current;
            }
        }
        finally
        {
            lock (gate)
            {
                Enter(PromptSessionState.Waiting);
            }
        }
    }

    /// <summary>
    /// Mixes the request into the work, taking it into the running prompt or the next one, and answers it with what that prompt's script says last.
    /// </summary>
    /// <param name="run">The request.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>One message with the answer.</returns>
    /// <exception cref="PromptSessionStateException">The session is responding.</exception>
    private async IAsyncEnumerable<PromptEvent> Mixed(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Check(run);

        TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Deliver(new Queued(run.Text, run.Attachments, run.Schema, completion, run.Notes));
        yield return new PromptMessageEvent { Text = await completion.Task.WaitAsync(cancellation) };
    }

    /// <summary>
    /// Runs the request in a copy of the session that is thrown away, so this one is not touched.
    /// </summary>
    /// <param name="run">The request.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The events of the copy's prompt.</returns>
    private async IAsyncEnumerable<PromptEvent> Forked(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        FakePromptSession fork;

        lock (gate)
        {
            fork = Copy(new FakePromptSession(Provider, ApiKey, sharedWindows, time));
        }

        try
        {
            await foreach (PromptEvent forked in fork.Direct(run with { Mode = PromptRequestMode.Direct }, cancellation))
            {
                yield return forked;
            }
        }
        finally
        {
            await fork.DisposeAsync();
        }
    }

    private void Deliver(Queued item)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (state == PromptSessionState.Responding)
            {
                throw new PromptSessionStateException(state, "The session is responding to a request and takes no input until it is waiting again.");
            }

            if (state == PromptSessionState.Working && workingOn is { } running && item.Attachments.Count == 0)
            {
                running.Take(item.Text, item.Notes);
                delivered.Add(item);
                return;
            }

            notes.AddRange(item.Notes);

            queued.Add(item);

            if (state == PromptSessionState.Waiting)
            {
                Enter(PromptSessionState.Working);
                pumping = Task.Run(Pump, CancellationToken.None);
            }
        }
    }

    private void Enter(PromptSessionState next)
    {
        state = next;

        output.Publish(new PromptStateEvent { State = next });
    }

    private async Task Pump()
    {
        while (true)
        {
            List<Queued> items;

            lock (gate)
            {
                if (queued.Count == 0 || disposed)
                {
                    queued.Clear();
                    Enter(PromptSessionState.Waiting);
                    return;
                }

                items = [.. queued];
                queued.Clear();
                delivered.Clear();
                delivered.AddRange(items);
            }

            List<PromptEvent> produced = [];
            Exception? failure = null;
            PromptRun run = new() { Text = string.Join("\n\n", items.Select(item => item.Text)), Attachments = [.. items.SelectMany(item => item.Attachments)], Schema = items.Select(item => item.Schema).FirstOrDefault(schema => schema is not null) };

            try
            {
                await foreach (PromptEvent next in Turn(run, closing.Token))
                {
                    produced.Add(next);
                    output.Publish(next);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                failure = new ObjectDisposedException(nameof(FakePromptSession));
            }
            catch (Exception exception)
            {
                failure = exception;
                output.Publish(new PromptFailureEvent { Message = exception.Message, Exception = exception });
            }

            string? answer = produced.OfType<PromptMessageEvent>().LastOrDefault()?.Text;
            List<Queued> answered;

            lock (gate)
            {
                answered = [.. delivered];
                delivered.Clear();
            }

            foreach (Queued item in answered.Where(item => item.Completion is not null))
            {
                if (failure is not null)
                {
                    item.Completion!.TrySetException(failure);
                }
                else if (answer is not null)
                {
                    item.Completion!.TrySetResult(answer);
                }
                else
                {
                    item.Completion!.TrySetException(new PromptSessionException("The AI finished its work without answering the request."));
                }
            }
        }
    }

    private async IAsyncEnumerable<PromptEvent> Turn(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Check(run);

        await turn.WaitAsync(cancellation);
        using CancellationTokenSource interruption = new();
        List<PromptEvent> produced = [];
        FakePrompt? prompt = null;

        lock (gate)
        {
            turnInterrupt = interruption;
            turnInterrupted = false;
        }

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            CheckBudget();
            using CancellationTokenSource running = CancellationTokenSource.CreateLinkedTokenSource(cancellation, interruption.Token, closing.Token);
            prompt = Start(run);

            lock (gate)
            {
                workingOn = prompt;
            }

            bool interrupted = false;

            await using IAsyncEnumerator<PromptEvent> events = Answer(prompt)(prompt, running.Token).GetAsyncEnumerator(running.Token);

            while (true)
            {
                try
                {
                    if (!await events.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (interruption.IsCancellationRequested && !cancellation.IsCancellationRequested && !closing.IsCancellationRequested)
                {
                    interrupted = true;
                    break;
                }

                produced.Add(events.Current);
                Count(events.Current);
                yield return events.Current;
            }

            if (interrupted || TurnInterrupted)
            {
                produced.Add(new PromptInterruptedEvent());
                yield return new PromptInterruptedEvent();
            }
        }
        finally
        {
            lock (gate)
            {
                turnInterrupt = null;
                workingOn = null;

                if (prompt is not null)
                {
                    exchanges.Add(new Exchange(prompt, produced));
                }
            }

            turn.Release();
        }
    }

    private bool TurnInterrupted
    {
        get
        {
            lock (gate)
            {
                return turnInterrupted;
            }
        }
    }

    private void Check(PromptRun run)
    {
        if (run.Schema is { } schema && !(schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out JsonElement type) && type.GetString() == "object"))
        {
            throw new ArgumentException("The schema must describe a JSON object, which the AI tools require of structured output.", nameof(run));
        }

    }

    private void CheckBudget()
    {
        PromptBudget? limit = settings.Limits.Budget;
        PromptUsage used;

        lock (gate)
        {
            used = usage;
        }

        if (limit is not null && limit.IsReached(used))
        {
            throw new PromptLimitException(PromptLimit.Budget, "The session has used what its budget allows.");
        }
    }

    private string NoModel
        => "No model is selected: set Configuration.Model first.";

    private void RequireModel()
    {
        if (settings.Model is null)
        {
            throw new PromptSessionModelException(NoModel);
        }
    }

    private FakePrompt Start(PromptRun run)
    {
        lock (gate)
        {
            FakePrompt prompt = new()
            {
                Time = time.GetUtcNow(),
                Text = run.Text,
                Attachments = run.Attachments,
                Schema = run.Schema,
                Model = settings.Model ?? throw new PromptSessionModelException(NoModel),
                ModelSettings = settings.ModelSettings.Copy(),
                Notes = [.. notes]
            };

            notes.Clear();

            prompts.Add(prompt);
            return prompt;
        }
    }

    private Script Answer(FakePrompt prompt)
    {
        lock (gate)
        {
            if (scripts.TryDequeue(out Script? script))
            {
                return script;
            }

            Func<FakePrompt, IEnumerable<PromptEvent>>? answer = responder;
            return answer is not null ? (asked, _) => answer(asked).ToAsyncEnumerable() : (asked, _) => Failing(new InvalidOperationException($"The fake session has no answer scripted for the prompt '{asked.Text}'. Script one with Reply, Fail, Hang or ReplyWith."));
        }
    }

    private void Count(PromptEvent output)
    {
        if (output is PromptUsageEvent used)
        {
            lock (gate)
            {
                usage = usage.Plus(used.Usage);
            }
        }
    }
}
