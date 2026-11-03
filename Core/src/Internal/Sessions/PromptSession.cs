namespace Markwardt.LiveAi;

/// <inheritdoc />
internal sealed class PromptSession : IPromptSession, IPromptCollector, IPromptExecutor, IPromptToolHooks
{
    private readonly string? apiKey;

    private readonly IPromptSessionStore store;

    private readonly IPromptProvider provider;

    private readonly IPromptEngine engine;

    private readonly ITurnRunner runner;

    private readonly IPromptToolHost toolHost;

    private readonly IPromptLibraryTools libraryTools;

    private readonly IPromptSessionOpener opener;

    private readonly PromptSessionSettings settings;

    private readonly SemaphoreSlim turn = new(1, 1);

    private readonly object gate = new();

    private readonly CancellationTokenSource closing = new();

    private readonly List<string> notes = [];

    private int notesInFlight;

    private readonly Broadcast<PromptEvent> output = new(false);

    private readonly List<Queued> queued = [];

    private readonly Dictionary<string, Pending> pending = [];

    private readonly string respondName = "respond";

    private PromptSessionState state = PromptSessionState.Waiting;

    private Task pumping = Task.CompletedTask;

    private PromptTool? respondTool;

    private PromptTool? askTool;

    private PromptSessionRecord current;

    private PromptSessionRecord? spawnedFrom;

    private string? spawnedSchema;

    private ToolEndpoint? spawnedTools;

    private IReadOnlyDictionary<string, IStore>? fileToolMounts;

    private PromptTool[]? fileTools;

    private PromptTool? shellTool;

    private string? shellFolder;

    private IStore? fileToolWorkspace;

    private IPromptToolServer? toolServer;

    private PromptTool[] servedTools = [];

    private volatile IPromptProcess? live;

    private volatile IPromptProcess? active;

    private Task teardown = Task.CompletedTask;

    private bool teardownScheduled;

    private PromptUsage usage = new();

    private CancellationTokenSource? turnInterrupt;

    private bool turnInterrupted;

    private bool instructionsKnown;

    private string? knownInstructions;

    private readonly List<Queued> delivered = [];

    private readonly IPromptRequester requester;

    private readonly IPromptLog log;

    private IPromptProcess? workProcess;

    private bool disposed;

    private Task? disposal;

    private PromptSessionRecord persisted;

    private PromptModel? asked;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptSession" /> class.
    /// </summary>
    /// <param name="record">What is stored about the session.</param>
    /// <param name="apiKey">The API key to run with, or <see langword="null" /> to use the configured subscription.</param>
    /// <param name="store">Keeps the record.</param>
    /// <param name="provider">The provider that made the session, which the session reports.</param>
    /// <param name="engine">Runs the prompts.</param>
    /// <param name="runner">Runs the engine's process.</param>
    /// <param name="toolHost">Starts the servers that offer the caller's tools.</param>
    /// <param name="libraryTools">Builds the library's file tools.</param>
    /// <param name="opener">Opens the sessions that branches are made into.</param>
    /// <param name="logger">The caller's logger, or <see langword="null" /> for none.</param>
    /// <param name="time">The clock the timeouts, retries and schedules of the session run on, or <see langword="null" /> for the system's.</param>
    public PromptSession(PromptSessionRecord record, string? apiKey, IPromptSessionStore store, IPromptProvider provider, IPromptEngine engine, ITurnRunner runner, IPromptToolHost toolHost, IPromptLibraryTools libraryTools, IPromptSessionOpener opener, ILogger? logger = null, TimeProvider? time = null)
    {
        current = record;
        persisted = record;
        log = new PromptLog(logger);
        Time = time ?? TimeProvider.System;
        this.apiKey = apiKey;
        this.store = store;
        this.provider = provider;
        this.engine = engine;
        this.runner = runner;
        this.toolHost = toolHost;
        this.libraryTools = libraryTools;
        this.opener = opener;
        requester = new PromptRequester(this, log, Time);
        settings = new PromptSessionSettings(record, [.. libraryTools.Names, libraryTools.ShellName, libraryTools.AskName, respondName]);
        settings.Changed += OnSettingsChanged;
    }

    /// <inheritdoc />
    public TimeProvider Time { get; }

    /// <inheritdoc />
    public string Id => current.Id.ToString("N");

    /// <inheritdoc />
    public IPromptProvider Provider => provider;

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

    private bool Stale
    {
        get
        {
            PromptSessionSettingsSnapshot snapshot = settings.Snapshot();

            lock (gate)
            {
                return spawnedFrom is { } started && !started.SameProcess(Merge(current, snapshot));
            }
        }
    }

    private bool Disposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
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

    /// <inheritdoc />
    public async Task<string> Request(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
        => (await Collect(input, options, null, cancellation)).Text;

    /// <inheritdoc />
    public Task<PromptResult> Collect(object input, PromptRequestOptions? options = null, PromptRequestLimits? limits = null, CancellationToken cancellation = default)
    {
        RequireModel();
        return requester.Collect(input, options ?? new(), settings.RequestLimits(limits), cancellation);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<PromptEvent> Execute(PromptRun run, CancellationToken cancellation = default)
    {
        RequireModel();

        return run.Mode switch
        {
            PromptRequestMode.Direct => Direct(run, cancellation),
            PromptRequestMode.Mixed => Mixed(run, cancellation),
            PromptRequestMode.Fork => Forked(run, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(run))
        };
    }

    /// <inheritdoc />
    public async Task Send(object input, PromptOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireModel();

        foreach (string note in options?.Notes ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(note);
        }

        string text = input is string literal ? literal : PromptJson.Serialize(input);
        IReadOnlyList<PromptAttachment> files = options?.Attachments ?? [];

        if (files.Count > 0)
        {
            _ = files.Prepare(text, engine);
        }

        await Deliver(new Queued(text, files, null, options?.Notes ?? []), cancellation);
    }

    /// <summary>
    /// Runs one turn of the main conversation with the engine and reports its events, once the conversation is free.
    /// </summary>
    /// <param name="run">What the AI is given.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The events of the turn.</returns>
    private async IAsyncEnumerable<PromptEvent> Turn(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        if (run.Schema is { } given && !IsObjectSchema(given))
        {
            throw new ArgumentException("The schema must describe a JSON object, which the AI tools require of structured output.", nameof(run));
        }

        (string prompt, IReadOnlyList<PromptAttachment> binary) = run.Attachments.Count == 0 ? (run.Text, run.Attachments) : run.Attachments.Prepare(run.Text, engine);
        ObjectDisposedException.ThrowIf(Disposed, this);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closing.Token);
        await turn.WaitAsync(linked.Token);
        using CancellationTokenSource interruption = new();

        lock (gate)
        {
            turnInterrupt = interruption;
            turnInterrupted = false;
        }

        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            using CancellationTokenSource running = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, interruption.Token);
            TurnStart? start = await Begin(run, running.Token, linked.Token);
            bool interrupted = start is null;

            if (start is not null)
            {
                bool delivered = false;
                await using IAsyncEnumerator<PromptEngineEvent> events = Run(start, prompt, binary, running.Token).GetAsyncEnumerator(running.Token);

                while (true)
                {
                    try
                    {
                        if (!await events.MoveNextAsync())
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (interruption.IsCancellationRequested && !linked.Token.IsCancellationRequested)
                    {
                        interrupted = true;
                        break;
                    }

                    PromptEngineEvent output = events.Current;

                    if (!delivered && output.Kind != PromptEngineEventKind.Failed)
                    {
                        delivered = true;
                        Delivered(start);
                    }

                    if (output.Kind == PromptEngineEventKind.Failed && TurnInterrupted)
                    {
                        continue;
                    }

                    if (output.Kind == PromptEngineEventKind.Failed)
                    {
                        throw new PromptSessionException(output.Text);
                    }

                    if (output.Kind == PromptEngineEventKind.Session && output.Text != current.AiSessionId)
                    {
                        Update(snapshot => snapshot with { AiSessionId = output.Text, ForkedFrom = null, ForkedAt = null });
                        await Persist(linked.Token);
                    }
                    else if (output.Kind == PromptEngineEventKind.Reply)
                    {
                        yield return new PromptMessageEvent { Text = output.Text };
                    }
                    else if (output.Kind == PromptEngineEventKind.Event)
                    {
                        if (output.Event is PromptUsageEvent used)
                        {
                            lock (gate)
                            {
                                usage = usage.Plus(used.Usage);
                            }
                        }

                        yield return output.Event!;
                    }
                }
            }

            if (interrupted || TurnInterrupted)
            {
                yield return new PromptInterruptedEvent();
            }
        }
        finally
        {
            lock (gate)
            {
                turnInterrupt = null;
                notesInFlight = 0;
            }

            turn.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> Interrupt(CancellationToken cancellation = default)
    {
        CancellationTokenSource? source;
        IPromptProcess? process;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            source = turnInterrupt;

            if (source is null)
            {
                return false;
            }

            turnInterrupted = true;
            process = live ?? active;
        }

        try
        {
            if (process is { IsRunning: true })
            {
                await process.Interrupt(cancellation);
            }
            else
            {
                await source.CancelAsync();
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default)
        => engine.GetWindows(cancellation);

    /// <inheritdoc />
    public Task<IPromptSession> Fork(PromptHistoryPoint? at = null, CancellationToken cancellation = default)
        => Branch(at, cancellation);

    /// <inheritdoc />
    public async Task Rewind(PromptHistoryPoint to, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closing.Token);
        await turn.WaitAsync(linked.Token);

        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            PromptSessionRecord source;

            lock (gate)
            {
                source = Merge(current, settings.Snapshot());
            }

            if ((source.AiSessionId ?? source.ForkedFrom) is null)
            {
                throw new PromptSessionException("The session has no conversation to rewind.");
            }

            PromptBranch branch = await engine.Branch(source.ToStart(apiKey, null, null), to, linked.Token);

            Update(record => record with { AiSessionId = branch.AiSessionId, ForkedFrom = branch.ForkedFrom, ForkedAt = branch.ForkedAt });
            await Persist(linked.Token);
            await Release(true);
            log.Moved(Id, "rewound");
        }
        finally
        {
            turn.Release();
        }
    }

    /// <inheritdoc />
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

        ObjectDisposedException.ThrowIf(Disposed, this);

        string? workspace = workspacePath is null ? null : Path.GetFullPath(workspacePath);

        if (workspace is not null && !Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"The workspace folder '{workspacePath}' does not exist.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closing.Token);
        await turn.WaitAsync(linked.Token);

        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            if (aiSessionId is not null && !engine.HasSession(aiSessionId))
            {
                throw new PromptSessionNotFoundException(aiSessionId, $"The {engine.Identifier} session {aiSessionId} does not exist.");
            }

            string folder = workspace ?? Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{current.Id:N}");
            Directory.CreateDirectory(folder);

            Update(record => record with { AiSessionId = aiSessionId, ForkedFrom = null, ForkedAt = null, WorkingFolder = folder, WorkspacePath = workspace, Sandbox = sandbox });

            lock (gate)
            {
                instructionsKnown = false;
                knownInstructions = null;
            }

            await Persist(linked.Token);
            await Release(true);
            log.Moved(Id, "attached to another conversation or workspace");
            Configuration.Workspace = workspace is null ? null : IStore.FromDisk(workspace, sandbox);
        }
        finally
        {
            turn.Release();
        }
    }

    /// <inheritdoc />
    public Task Compact(CancellationToken cancellation = default)
        => Operate(async (process, token) =>
        {
            await foreach (PromptEngineEvent output in process.Compact(token))
            {
                if (output.Kind == PromptEngineEventKind.Failed)
                {
                    throw new PromptSessionException(output.Text);
                }

                if (output.Kind == PromptEngineEventKind.Session && output.Text != current.AiSessionId)
                {
                    Update(record => record with { AiSessionId = output.Text, ForkedFrom = null, ForkedAt = null });
                    await Persist(token);
                }
            }

            return true;
        }, cancellation);

    /// <inheritdoc />
    public Task<PromptContext> GetContext(CancellationToken cancellation = default)
        => Operate((process, token) => process.GetContext(token), cancellation);

    /// <inheritdoc />
    public IAsyncEnumerable<PromptHistoryEntry> GetHistory(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return ReadHistory(cancellation);
    }

    private async IAsyncEnumerable<PromptHistoryEntry> ReadHistory([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        string? origin;

        lock (gate)
        {
            origin = current.AiSessionId ?? current.ForkedFrom;
        }

        if (origin is null)
        {
            yield break;
        }

        await using IAsyncEnumerator<PromptHistoryEntry> entries = engine.ReadHistory(origin, cancellation).GetAsyncEnumerator(cancellation);

        while (true)
        {
            try
            {
                if (!await entries.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (IOException exception)
            {
                throw new PromptSessionException($"The conversation could not be read: {exception.Message}", exception);
            }

            yield return entries.Current;
        }
    }

    /// <inheritdoc />
    public void Dispose()
        => _ = Close(true).ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>
    /// Takes over what is known of the instructions the conversation holds, for a branch of a session whose conversation it continues.
    /// </summary>
    /// <param name="known">Whether the instructions of the conversation are known.</param>
    /// <param name="instructions">The instructions the conversation holds, or <see langword="null" /> for none.</param>
    internal void Inherit(bool known, string? instructions)
    {
        lock (gate)
        {
            instructionsKnown = known;
            knownInstructions = instructions;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
        => await Close(false);

    /// <summary>
    /// Runs the request in the main conversation, with the session responding to it meanwhile.
    /// </summary>
    /// <param name="run">The request.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The events of the turn.</returns>
    /// <exception cref="PromptSessionStateException">The session is not waiting.</exception>
    private async IAsyncEnumerable<PromptEvent> Direct(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

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
                    log.WorkFailed(Id, exception);
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
    /// Mixes the request into the work of the AI and waits for it to answer through the respond tool.
    /// </summary>
    /// <param name="run">The request.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>One message with the answer.</returns>
    /// <exception cref="PromptSessionStateException">The session is responding.</exception>
    /// <exception cref="NotSupportedException">The request has a tool or mount of its own, which the turn it is mixed into does not take.</exception>
    private async IAsyncEnumerable<PromptEvent> Mixed(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (run.Schema is { } given && !IsObjectSchema(given))
        {
            throw new ArgumentException("The schema must describe a JSON object, which the AI tools require of structured output.", nameof(run));
        }

        _ = run.Attachments.Prepare(run.Text, engine);

        string id = $"r{Guid.NewGuid():N}"[..13];
        Pending waiting = new(run.Schema);
        string shape = run.Schema is { } schema ? $" as an object that follows this JSON Schema: {schema.GetRawText()}" : " as text";
        string text = $"Request {id}: {run.Text}\n\nAnswer this request by calling the {respondName} tool with request_id \"{id}\" and your answer{shape}. Carry on with the rest of your work: the answer goes through the tool and not through your message.";

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending[id] = waiting;
        }

        string answer;

        try
        {
            await Deliver(new Queued(text, run.Attachments, id, run.Notes), cancellation);
            answer = await waiting.Completion.Task.WaitAsync(cancellation);
        }
        finally
        {
            lock (gate)
            {
                pending.Remove(id);
            }
        }

        yield return new PromptMessageEvent { Text = answer };
    }

    /// <summary>
    /// Runs the request in a fork of the conversation, which is thrown away afterwards.
    /// </summary>
    /// <param name="run">The request.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The events of the fork's turn.</returns>
    private async IAsyncEnumerable<PromptEvent> Forked(PromptRun run, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        PromptSession fork = await Branch(null, cancellation, false) as PromptSession ?? throw new InvalidOperationException("A fork of a session is a session of the same kind.");

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

            try
            {
                await provider.Delete(fork.Id);
            }
            catch (Exception exception) when (exception is PromptSessionException or ArgumentException or IOException)
            {
            }
        }
    }

    /// <summary>
    /// Gives input to the AI at once: into the work it is doing when its process takes it, and otherwise as input that waits for the next piece of work.
    /// </summary>
    /// <param name="item">The input.</param>
    /// <param name="cancellation">Cancels the delivery.</param>
    /// <returns>A task that completes when the input was written to the running AI process, or was queued for the next work, which is before the AI acts on it.</returns>
    /// <exception cref="PromptSessionStateException">The session is responding.</exception>
    private async Task Deliver(Queued item, CancellationToken cancellation = default)
    {
        IPromptProcess? target = null;
        PromptModel? model;
        IReadOnlyDictionary<string, string> modelSettings;
        string[] told;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (state == PromptSessionState.Responding)
            {
                throw new PromptSessionStateException(state, "The session is responding to a request and takes no input until it is waiting again.");
            }

            notes.AddRange(item.Notes);

            if (state == PromptSessionState.Working && workProcess is { } running && !(engine.NeedsFiles && item.Attachments.Count > 0))
            {
                target = running;
            }

            if (target is null)
            {
                Queue(item);
                return;
            }

            model = current.Model;
            modelSettings = current.ModelSettings;
            told = [.. notes.Skip(notesInFlight)];
            notes.RemoveRange(notesInFlight, told.Length);
            delivered.Add(item);
        }

        (string prompt, IReadOnlyList<PromptAttachment> binary) = item.Attachments.Count == 0 ? (item.Text, item.Attachments) : item.Attachments.Prepare(item.Text, engine);
        bool taken = false;

        try
        {
            taken = await target.Steer(new PromptInput { Text = prompt, Attachments = binary, Model = model?.Id ?? string.Empty, Settings = modelSettings, Notes = told }, cancellation);
        }
        catch (Exception)
        {
            Withdraw(item, told);
            throw;
        }

        log.Steered(Id, taken);

        if (taken)
        {
            return;
        }

        lock (gate)
        {
            delivered.Remove(item);
            notes.AddRange(told);

            if (state == PromptSessionState.Responding)
            {
                throw new PromptSessionStateException(state, "The session is responding to a request and takes no input until it is waiting again.");
            }

            if (!disposed)
            {
                Queue(item);
            }
        }
    }

    /// <summary>
    /// Takes input back that was handed to a process but did not arrive: it is no longer delivered, and its notes wait again.
    /// </summary>
    /// <param name="item">The input.</param>
    /// <param name="told">The notes that went with it.</param>
    private void Withdraw(Queued item, string[] told)
    {
        lock (gate)
        {
            delivered.Remove(item);
            notes.AddRange(told);
        }
    }

    /// <summary>
    /// Adds input to what waits for the AI and starts the pump when the session was waiting. The caller holds the lock.
    /// </summary>
    /// <param name="item">The input.</param>
    private void Queue(Queued item)
    {
        queued.Add(item);

        if (state == PromptSessionState.Waiting)
        {
            Enter(PromptSessionState.Working);
            pumping = Task.Run(Pump, CancellationToken.None);
        }
    }

    /// <summary>
    /// Changes the state and reports it. The caller holds the lock.
    /// </summary>
    /// <param name="next">The new state.</param>
    private void Enter(PromptSessionState next)
    {
        state = next;
        log.State(Id, next);

        output.Publish(new PromptStateEvent { State = next });
    }

    /// <summary>
    /// Gives the input that waits to the AI as work, one piece after the other, until none is left.
    /// </summary>
    /// <returns>A task that completes when the session is waiting again.</returns>
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

            Exception? failure = null;

            try
            {
                await foreach (PromptEvent produced in Turn(new PromptRun { Text = string.Join("\n\n", items.Select(item => item.Text)), Attachments = [.. items.SelectMany(item => item.Attachments)] }, closing.Token))
                {
                    output.Publish(produced);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                failure = new ObjectDisposedException(nameof(PromptSession));
            }
            catch (Exception exception)
            {
                failure = exception;
                log.WorkFailed(Id, exception);
                output.Publish(new PromptFailureEvent { Message = exception.Message, Exception = exception });
            }

            lock (gate)
            {
                foreach (Queued item in delivered.Where(item => item.Request is not null))
                {
                    if (pending.TryGetValue(item.Request!, out Pending? waiting))
                    {
                        waiting.Completion.TrySetException(failure ?? new PromptSessionException("The AI finished its work without answering the request."));
                    }
                }

                delivered.Clear();
            }
        }
    }

    /// <summary>
    /// Gets the tool the AI answers mixed requests with, made when it is first needed.
    /// </summary>
    /// <returns>The tool.</returns>
    private PromptTool AskTool()
        => askTool ??= libraryTools.CreateAsk(Ask);

    private async Task<string> Ask(string question, CancellationToken cancellation)
        => settings.Asker is { } asker ? await asker(question, cancellation) : throw new InvalidOperationException("No one is available to answer questions right now.");

    private PromptTool RespondTool()
        => respondTool ??= PromptTool.Create<RespondInput, string>(respondName, "Answers a request that came with an identifier. Give the identifier as request_id and the answer, which is an object when the request says it follows a schema. The answer is recorded and you carry on with your work.", (input, _) => Task.FromResult(Answer(input)));

    /// <summary>
    /// Records an answer the AI gave through the respond tool.
    /// </summary>
    /// <param name="input">What the AI gave.</param>
    /// <returns>The reply to the AI.</returns>
    /// <exception cref="ArgumentException">No such request is waiting, or the answer does not fit what was asked, which the AI is told so that it can answer again.</exception>
    private string Answer(RespondInput input)
    {
        Pending? waiting;

        lock (gate)
        {
            pending.TryGetValue(input.RequestId, out waiting);
        }

        if (waiting is null)
        {
            throw new ArgumentException($"No request with the identifier '{input.RequestId}' is waiting for an answer.");
        }

        if (waiting.Schema is { } schema)
        {
            if (input.Answer.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The answer has to be an object that follows the schema of the request.");
            }

            if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array && required.EnumerateArray().Select(name => name.GetString()).FirstOrDefault(name => name is not null && !input.Answer.TryGetProperty(name, out _)) is { } missing)
            {
                throw new ArgumentException($"The answer is missing the property '{missing}' that the schema of the request requires.");
            }

            waiting.Completion.TrySetResult(input.Answer.GetRawText());
        }
        else
        {
            waiting.Completion.TrySetResult(input.Answer.ValueKind == JsonValueKind.String ? input.Answer.GetString() ?? string.Empty : input.Answer.GetRawText());
        }

        return "Recorded.";
    }

    private async Task<TurnStart?> Begin(PromptRun run, CancellationToken running, CancellationToken persisting)
    {
        try
        {
            await Verify(running);
            PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
            CheckBudget(snapshot.Limits.Budget);
            Update(record => Merge(record, snapshot));
            await PersistModel(running);

            PromptSessionRecord effective;
            List<string> given;
            int told;

            lock (gate)
            {
                effective = current;
                told = notes.Count;
                notesInFlight = told;
                given = [.. notes];

                if (engine.KeepsInstructions && effective.AiSessionId is not null && (instructionsKnown ? knownInstructions != effective.Instructions : effective.Instructions is not null))
                {
                    given.Add(effective.Instructions is null ? "The additional instructions given earlier in this conversation no longer apply." : $"The additional instructions for this conversation have changed. From now on follow these, in place of any given earlier:\n\n{effective.Instructions}");
                }
            }

            return new TurnStart(effective, run.Schema is { } formatted ? engine.FormatSchema(formatted) : null, await Serve(snapshot, running), snapshot.IsPersistent, given, told);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !persisting.IsCancellationRequested)
        {
            return null;
        }
    }

    private void Delivered(TurnStart start)
    {
        lock (gate)
        {
            notes.RemoveRange(0, Math.Min(start.Told, notes.Count));
            notesInFlight = 0;
            instructionsKnown = true;
            knownInstructions = start.Record.Instructions;
        }
    }

    private bool IsObjectSchema(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object";

    private async IAsyncEnumerable<PromptEngineEvent> Run(TurnStart start, string prompt, IReadOnlyList<PromptAttachment> attachments, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        PromptInput prompting = new() { Text = prompt, Attachments = attachments, Schema = start.Schema, Model = start.Record.Model?.Id ?? string.Empty, Settings = start.Record.ModelSettings, Notes = start.Notes };

        if (engine.CanPersist)
        {
            bool keep = start.Persistent;

            if (!keep)
            {
                await Release(false);
            }

            IPromptProcess process = keep ? await Open(start.Record, start.Schema, start.Endpoint) : Launch(start.Record, start.Schema, start.Endpoint);
            (string? folder, IReadOnlyList<string> files) = engine.NeedsFiles && attachments.Count > 0 ? await WriteFiles(attachments, cancellation) : (null, []);
            lock (gate)
            {
                if (state == PromptSessionState.Working && queued.Count > 0)
                {
                    List<Queued> joined = [.. queued.Where(item => item.Attachments.Count == 0)];

                    if (joined.Count > 0)
                    {
                        string[] told = [.. joined.SelectMany(item => item.Notes)];

                        foreach (string note in told)
                        {
                            int index = notes.LastIndexOf(note);

                            if (index >= notesInFlight)
                            {
                                notes.RemoveAt(index);
                            }
                        }

                        prompting = prompting with { Text = string.Join("\n\n", [prompting.Text, .. joined.Select(item => item.Text)]), Notes = [.. prompting.Notes, .. told] };
                        queued.RemoveAll(joined.Contains);
                        delivered.AddRange(joined);
                    }
                }

                active = process;
                workProcess = process;
            }

            try
            {
                await foreach (PromptEngineEvent output in process.Run(files.Count > 0 ? prompting with { Attachments = [], Files = files } : prompting, cancellation))
                {
                    yield return output;
                }
            }
            finally
            {
                lock (gate)
                {
                    workProcess = null;
                }

                active = null;

                if (!keep)
                {
                    await process.DisposeAsync();
                }

                if (folder is not null && Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
        }
        else
        {
            await Release(false);
            await foreach (PromptEngineEvent output in attachments.Count == 0 ? runner.Run(engine, engine.CreateStart(start.Record.ToStart(apiKey, start.Schema, start.Endpoint)), engine.Plain(prompting), cancellation) : RunWithAttachments(start, prompting, cancellation))
            {
                yield return output;
            }
        }
    }

    private async IAsyncEnumerable<PromptEngineEvent> RunWithAttachments(TurnStart start, PromptInput prompting, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        (string? folder, IReadOnlyList<string> files) = engine.NeedsFiles ? await WriteFiles(prompting.Attachments, cancellation) : (null, []);

        try
        {
            await foreach (PromptEngineEvent output in runner.Run(engine, engine.CreateAttachmentStart(start.Record.ToStart(apiKey, start.Schema, start.Endpoint, files)), engine.Frame(prompting with { Files = files }), cancellation))
            {
                yield return output;
            }
        }
        finally
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    private async Task<(string Folder, IReadOnlyList<string> Files)> WriteFiles(IReadOnlyList<PromptAttachment> attachments, CancellationToken cancellation = default)
    {
        string folder = Path.Combine(current.WorkspacePath is null ? current.WorkingFolder : Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        List<string> files = [];

        foreach ((PromptAttachment attachment, int index) in attachments.Select((attachment, index) => (attachment, index)))
        {
            string path = Path.Combine(folder, $"attachment-{index}{attachment.MediaType.ToExtension()}");
            await File.WriteAllBytesAsync(path, attachment.Content, cancellation);
            files.Add(path);
        }

        return (folder, files);
    }

    private IPromptProcess Launch(PromptSessionRecord record, string? schema, ToolEndpoint? endpoint)
    {
        PromptEngineStart given = record.ToStart(apiKey, schema, endpoint);
        log.ProcessStarted(Id, engine.Identifier);
        return runner.Open(engine, engine.CreatePersistentStart(given), given);
    }

    private async Task<IPromptProcess> Open(PromptSessionRecord effective, string? schema, ToolEndpoint? endpoint)
    {
        if (live is { IsRunning: true } running && spawnedFrom is { } started && started.SameProcess(effective) && (engine.SchemaPerTurn || spawnedSchema == schema) && spawnedTools == endpoint)
        {
            return running;
        }

        await Release(true);
        spawnedFrom = effective;
        spawnedSchema = schema;
        spawnedTools = endpoint;
        live = Launch(effective, schema, endpoint);
        return live;
    }

    private async Task<IPromptSession> Branch(PromptHistoryPoint? at, CancellationToken cancellation = default, bool exclusive = true)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closing.Token);
        if (exclusive)
        {
            await turn.WaitAsync(linked.Token);
        }

        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
            PromptSessionRecord source;

            lock (gate)
            {
                source = Merge(current, snapshot);
            }

            if (at is not null && (source.AiSessionId ?? source.ForkedFrom) is null)
            {
                throw new PromptSessionException("The session has no conversation to branch at a point of.");
            }

            PromptBranch branch = (source.AiSessionId ?? source.ForkedFrom) is null ? new PromptBranch() : await engine.Branch(source.ToStart(apiKey, null, null), at, linked.Token);
            Guid id = Guid.NewGuid();
            string folder = source.WorkspacePath ?? Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id:N}");
            Directory.CreateDirectory(folder);

            PromptSessionLink link = new() { Id = id, Created = Time.GetUtcNow(), Provider = source.Engine.Identifier, WorkingFolder = folder, WorkspacePath = source.WorkspacePath, Sandbox = source.Sandbox, AiSessionId = branch.AiSessionId, ForkedFrom = branch.ForkedFrom, ForkedAt = branch.ForkedAt, Model = source.Model?.Id, ModelSettings = source.Model is null ? null : new Dictionary<string, string>(source.ModelSettings) };
            await store.Save(link, linked.Token);

            IPromptSession fork = opener.Open(link, provider, engine, apiKey);

            if (fork is PromptSession inheriting)
            {
                lock (gate)
                {
                    inheriting.Inherit(instructionsKnown, knownInstructions);
                }
            }

            snapshot.CopyTo(fork.Configuration);
            log.Moved(Id, $"forked into session {fork.Id}");

            return fork;
        }
        catch (IOException exception)
        {
            throw new PromptSessionException($"The session could not be branched: {exception.Message}", exception);
        }
        finally
        {
            if (exclusive)
            {
                turn.Release();
            }
        }
    }

    private async Task<TResult> Operate<TResult>(Func<IPromptProcess, CancellationToken, Task<TResult>> operation, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (!engine.CanPersist)
        {
            throw new NotSupportedException($"{engine.Identifier} cannot do this.");
        }

        RequireModel();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closing.Token);
        await turn.WaitAsync(linked.Token);

        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            await Verify(linked.Token);
            PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
            Update(record => Merge(record, snapshot));

            PromptSessionRecord effective;

            lock (gate)
            {
                effective = current;
            }

            ToolEndpoint? endpoint = await Serve(snapshot, linked.Token);
            bool keep = snapshot.IsPersistent;

            if (!keep)
            {
                await Release(false);
            }

            IPromptProcess process = keep ? await Open(effective, null, endpoint) : Launch(effective, null, endpoint);
            active = process;

            try
            {
                return await operation(process, linked.Token);
            }
            finally
            {
                active = null;

                if (!keep)
                {
                    await process.DisposeAsync();
                }
            }
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task<ToolEndpoint?> Serve(PromptSessionSettingsSnapshot snapshot, CancellationToken cancellation = default)
    {
        PromptTool[] exposed;
        bool hasFiles;
        bool hasShell;
        bool inWorkspace;
        IStore? workspace;

        lock (gate)
        {
            Dictionary<string, IStore> available = new(snapshot.Mounts);
            workspace = snapshot.Workspace;
            hasFiles = workspace is not null || available.Count > 0;
            hasShell = snapshot.EnableShell;
            inWorkspace = workspace is IDiskStore;

            if (hasShell)
            {
                string folder = workspace is IDiskStore disk ? disk.Path : current.WorkingFolder;

                if (shellTool is null || shellFolder != folder)
                {
                    shellTool = libraryTools.CreateShell(folder);
                    shellFolder = folder;
                }
            }

            if (fileToolMounts is null || fileToolWorkspace != workspace || fileToolMounts.Count != snapshot.Mounts.Count || snapshot.Mounts.Any(mount => !fileToolMounts.TryGetValue(mount.Key, out IStore? existing) || existing != mount.Value))
            {
                fileTools = null;
                fileToolMounts = snapshot.Mounts;
                fileToolWorkspace = workspace;
            }

            PromptTool[] files = !hasFiles ? [] : fileTools ??= libraryTools.Create(new MountedStore(workspace, available), available.Keys.ToArray(), workspace is not null).ToArray();
            exposed = files.Concat(hasShell ? [shellTool!] : []).Concat(snapshot.Tools).Concat(snapshot.Asker is null ? [] : [AskTool()]).Append(RespondTool()).ToArray();

            if (exposed.GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } clash)
            {
                throw new PromptSessionException($"The AI would be offered two tools named '{clash.Key}'.");
            }
        }

        cancellation.ThrowIfCancellationRequested();

        if (toolServer is null || !exposed.SequenceEqual(servedTools))
        {
            await StopTools();
            toolServer = toolHost.Start(exposed, this);
            servedTools = exposed;
            log.ToolServerStarted(Id, exposed.Length);
        }

        return toolServer.Endpoint with { HasFiles = hasFiles, HasShell = hasShell, HasWorkspace = workspace is not null, ShellInWorkspace = hasShell && inWorkspace };
    }

    /// <inheritdoc />
    public Task<PromptToolDecision> Before(PromptToolCallEvent call, CancellationToken cancellation = default)
        => settings.BeforeToolCall is { } hook ? hook(call, cancellation) : Task.FromResult(PromptToolDecision.Allow);

    /// <inheritdoc />
    public Task After(PromptToolCallEvent call, PromptToolResultEvent result, TimeSpan duration, CancellationToken cancellation = default)
        => settings.AfterToolCall is { } hook ? hook(call, result, duration, cancellation) : Task.CompletedTask;

    private void CheckBudget(PromptBudget? limit)
    {
        PromptUsage used;

        lock (gate)
        {
            used = usage;
        }

        if (limit is not null && limit.IsReached(used))
        {
            log.LimitReached(PromptLimit.Budget);
            throw new PromptLimitException(PromptLimit.Budget, "The session has used what its budget allows.");
        }
    }

    private async Task StopTools()
    {
        IPromptToolServer? server = toolServer;
        toolServer = null;
        servedTools = [];

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    private async Task Release(bool immediate)
    {
        IPromptProcess? process = live;
        live = null;

        if (process is null)
        {
            return;
        }

        if (immediate)
        {
            process.Stop();
        }

        await process.DisposeAsync();
    }

    private async Task TearDown()
    {
        lock (gate)
        {
            teardownScheduled = false;
        }

        try
        {
            await turn.WaitAsync();

            try
            {
                if (!settings.IsPersistent)
                {
                    await Release(false);
                }
                else if (Stale)
                {
                    await Release(true);
                }
            }
            finally
            {
                turn.Release();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Win32Exception or ObjectDisposedException)
        {
        }
    }

    private Task Close(bool immediate)
    {
        Task closed;

        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                disposal = Task.Run(() => Finish(immediate), CancellationToken.None);
            }

            closed = disposal!;
        }

        settings.Close();

        if (immediate)
        {
            closing.Cancel();
            live?.Stop();
        }

        return closed;
    }

    private async Task Finish(bool immediate)
    {
        await turn.WaitAsync();

        try
        {
            await Release(immediate);
            await StopTools();
        }
        finally
        {
            turn.Release();
        }

        await pumping.ContinueWith(static _ => { }, CancellationToken.None);

        lock (gate)
        {
            foreach (Pending waiting in pending.Values)
            {
                waiting.Completion.TrySetException(new ObjectDisposedException(nameof(PromptSession)));
            }
        }

        output.Complete();
        await teardown;
    }

    private void OnSettingsChanged()
    {
        lock (gate)
        {
            if (disposed || teardownScheduled || live is null)
            {
                return;
            }

            teardownScheduled = true;
            teardown = Task.WhenAll(teardown, Task.Run(TearDown, CancellationToken.None));
        }
    }

    private PromptSessionRecord Merge(PromptSessionRecord record, PromptSessionSettingsSnapshot snapshot)
        => record with { Model = snapshot.Model, ModelSettings = snapshot.ModelSettings, Instructions = snapshot.Instructions, McpServers = new Dictionary<string, PromptMcpServer>(snapshot.McpServers) };

    private void Update(Func<PromptSessionRecord, PromptSessionRecord> change)
    {
        lock (gate)
        {
            current = change(current);
        }
    }

    private async Task Persist(CancellationToken cancellation = default)
    {
        PromptSessionLink link;

        PromptSessionRecord saving;

        lock (gate)
        {
            saving = current;
            link = saving.ToLink();
        }

        await store.Save(link, cancellation);

        lock (gate)
        {
            persisted = saving;
        }
    }

    private async Task PersistModel(CancellationToken cancellation)
    {
        bool changed;

        lock (gate)
        {
            changed = !current.SameModel(persisted);
        }

        if (changed)
        {
            await Persist(cancellation);
        }
    }

    private PromptModel RequireModel()
        => settings.Model ?? throw new PromptSessionModelException("No model is selected: set Configuration.Model first.");

    private async Task Verify(CancellationToken cancellation)
    {
        PromptModel model = RequireModel();

        if (model.Settings is not null || ReferenceEquals(model, asked))
        {
            return;
        }

        if ((await provider.GetModels(apiKey, cancellation)).Count == 0)
        {
            asked = model;
            return;
        }

        try
        {
            settings.Model = await provider.GetModel(model.Id, apiKey, cancellation);
        }
        catch (PromptSessionModelException)
        {
            if (ReferenceEquals(settings.Model, model))
            {
                settings.Model = null;
            }

            log.ModelChanged(Id, $"The model {model.Id} is no longer offered by {engine.Identifier}, so the session has none selected");
            throw new PromptSessionModelException($"The model '{model.Id}' is no longer offered by {engine.Identifier}. Select another with Configuration.Model.");
        }
    }

    private sealed record Queued(string Text, IReadOnlyList<PromptAttachment> Attachments, string? Request, IReadOnlyList<string> Notes);

    private sealed record RespondInput([property: JsonPropertyName("request_id")] string RequestId, JsonElement Answer);

    private sealed class Pending(JsonElement? schema)
    {
        public JsonElement? Schema { get; } = schema;

        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record TurnStart(PromptSessionRecord Record, string? Schema, ToolEndpoint? Endpoint, bool Persistent, IReadOnlyList<string> Notes, int Told);
}
