namespace Markwardt.LiveAi;

/// <inheritdoc />
internal sealed class PromptSession : IPromptSession, IPromptCollector, IPromptExecutor
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

    private readonly Broadcast<PromptEvent> output = new();

    private readonly List<Queued> queued = [];

    private readonly Dictionary<string, Pending> pending = [];

    private readonly string respondName = "respond";

    private readonly string delegatedNote = "A task was delegated to you by another AI that is working with the user and cannot see your work. Do it, and answer with the result only: what you found or did, in a form the other AI can use as it is.";

    private readonly string sharedNote = "A task was delegated to you by another AI that is working with the user, and you continue its conversation as it stood before its current request. Do the task, and answer with the result only: what you found or did, in a form the other AI can use as it is.";

    private readonly string imageNote = "This message was sent by the view_image tool you called to look at an image, not typed by the user.";

    private PromptSessionState state = PromptSessionState.Waiting;

    private Task pumping = Task.CompletedTask;

    private PromptTool? respondTool;

    private PromptTool? askTool;

    private PromptTool? imageTool;

    private PromptTool? fetchTool;

    private PromptTool? searchTool;

    private PromptTool? generateTool;

    private PromptImageOptions? generateWith;

    private readonly Func<PromptImageOptions, IPromptImageGenerator> generators;

    private IPromptImageGenerator? imageGenerator;

    private PromptTool? timeTool;

    private PromptTool? toolSearchTool;

    private PromptTool? delegateTool;

    private readonly SemaphoreSlim delegating = new(3, 3);

    private bool isHelper;

    private string? turnText;

    private PromptSessionRecord current;

    private PromptSessionRecord? spawnedFrom;

    private string? spawnedSchema;

    private ToolEndpoint? spawnedTools;

    private IReadOnlyDictionary<string, string> descriptors;

    private DiskStore workspaceStore;

    private IStore cache = new MemoryStore();

    private bool ownsCache = true;

    private PromptTool[]? fileTools;

    private PromptTool? shellTool;

    private string? shellFolder;

    private DiskStore? fileToolWorkspace;

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

    private (PromptCapability Capability, PromptEffort Effort)? selected;

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
    /// <param name="generators">Makes the generator for image options, or <see langword="null" /> to make it by the type of the options.</param>
    public PromptSession(PromptSessionRecord record, string? apiKey, IPromptSessionStore store, IPromptProvider provider, IPromptEngine engine, ITurnRunner runner, IPromptToolHost toolHost, IPromptLibraryTools libraryTools, IPromptSessionOpener opener, ILogger? logger = null, TimeProvider? time = null, Func<PromptImageOptions, IPromptImageGenerator>? generators = null)
    {
        this.generators = generators ?? (options => options.CreateGenerator());
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
        requester = new PromptRequester(this);
        settings = new PromptSessionSettings(record, [.. libraryTools.GetReservedNames(), respondName]);
        settings.Changed += OnSettingsChanged;
        workspaceStore = new DiskStore(Path.GetFullPath(record.WorkingFolder), record.IsSandboxed);
        descriptors = Describe(record);
    }

    /// <inheritdoc />
    public TimeProvider Time { get; }

    /// <inheritdoc />
    public string WorkspacePath
    {
        get
        {
            lock (gate)
            {
                return current.WorkingFolder;
            }
        }
    }

    /// <inheritdoc />
    public IStore Cache
    {
        get
        {
            lock (gate)
            {
                return cache;
            }
        }
    }

    /// <summary>
    /// Gets the store the AI's file tools work on: the workspace, if there is one, with the cache at its place in the top level.
    /// </summary>
    internal IStore Files
    {
        get
        {
            lock (gate)
            {
                SyncWorkspace(settings.IsSandboxed);
                return new MountedStore(workspaceStore, new Dictionary<string, IStore> { [libraryTools.CacheName] = cache });
            }
        }
    }

    /// <inheritdoc />
    public string Id => current.Id.ToString("N");

    /// <inheritdoc />

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
    public IPromptSessionTransientConfiguration Transient => settings;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Descriptors
    {
        get
        {
            lock (gate)
            {
                return descriptors;
            }
        }
    }

    /// <summary>
    /// Gets the settings, with the tools, the external servers and the asker.
    /// </summary>
    internal PromptSessionSettings Settings => settings;

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
    public IDisposable Subscribe(IObserver<PromptEvent> observer)
        => output.Subscribe(observer);

    /// <inheritdoc />
    public async Task<string> Request(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
        => (await Collect(input, options, cancellation)).Text;

    /// <inheritdoc />
    public Task<PromptResult> Collect(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
    {
        return requester.Collect(input, options ?? new(), cancellation);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<PromptEvent> Execute(PromptRun run, CancellationToken cancellation = default)
    {

        return run.Mode switch
        {
            PromptRequestMode.Direct => Direct(run, cancellation),
            PromptRequestMode.Mixed => Mixed(run, cancellation),
            PromptRequestMode.Fork => Forked(run, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(run))
        };
    }

    /// <inheritdoc />
    public async Task Send(object input, PromptSendOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(input);

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

        await Deliver(new Queued(text, files, null, options?.Notes ?? []), false, cancellation);
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
            turnText = prompt;
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

            try
            {
                await Persist(linked.Token);
            }
            catch
            {
                Update(record => record with { AiSessionId = source.AiSessionId, ForkedFrom = source.ForkedFrom, ForkedAt = source.ForkedAt });
                throw;
            }

            await Release(true);
            log.Moved(Id, "rewound");
        }
        finally
        {
            turn.Release();
        }
    }

    /// <inheritdoc />
    public async Task Move(string? workspacePath, CancellationToken cancellation = default)
    {
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

            string folder = workspace ?? store.GetWorkspaceFolder(current.Id);
            bool made = !Directory.Exists(folder);
            Directory.CreateDirectory(folder);

            PromptSessionRecord source;

            lock (gate)
            {
                source = current;
            }

            try
            {
                if ((source.AiSessionId ?? source.ForkedFrom) is { } origin)
                {
                    await engine.MoveSession(origin, folder, linked.Token);
                }

                Update(record => record with { WorkingFolder = folder, WorkspacePath = workspace });

                lock (gate)
                {
                    workspaceStore = new DiskStore(folder, settings.IsSandboxed);
                }

                await Persist(linked.Token);
            }
            catch
            {
                Update(record => record with { WorkingFolder = source.WorkingFolder, WorkspacePath = source.WorkspacePath });

                lock (gate)
                {
                    workspaceStore = new DiskStore(source.WorkingFolder, settings.IsSandboxed);
                }

                if (made)
                {
                    Directory.Delete(folder, true);
                }

                throw;
            }

            await Release(true);
            log.Moved(Id, "moved to another workspace");

            if (source.WorkspacePath is null && workspace is not null && source.WorkingFolder != folder && Directory.Exists(source.WorkingFolder))
            {
                try
                {
                    Directory.Delete(source.WorkingFolder, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
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

                if (queued.Count > 0 && !disposed)
                {
                    Enter(PromptSessionState.Working);
                    pumping = Task.Run(Pump, CancellationToken.None);
                }
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
    /// <exception cref="NotSupportedException">The request has a tool of its own, which the turn it is mixed into does not take.</exception>
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
            await Deliver(new Queued(text, run.Attachments, id, run.Notes), false, cancellation);
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
    /// <param name="fromTool">Whether a tool of the AI gives the input while a request is being answered, which is accepted in the state the session is responding in: it is steered into the work the AI is doing, and otherwise waits until the request is over.</param>
    /// <param name="cancellation">Cancels the delivery.</param>
    /// <returns>A task that completes when the input was written to the running AI process, or was queued for the next work, which is before the AI acts on it.</returns>
    /// <exception cref="PromptSessionStateException">The session is responding and the input does not come from a tool.</exception>
    private async Task Deliver(Queued item, bool fromTool = false, CancellationToken cancellation = default)
    {
        IPromptProcess? target = null;
        PromptModel? model;
        IReadOnlyDictionary<string, string> modelSettings;
        string[] told;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (state == PromptSessionState.Responding && !fromTool)
            {
                throw new PromptSessionStateException(state, "The session is responding to a request and takes no input until it is waiting again.");
            }

            notes.AddRange(item.Notes);

            if (state is PromptSessionState.Working or PromptSessionState.Responding && workProcess is { } running && !(engine.NeedsFiles && item.Attachments.Count > 0))
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

            if (state == PromptSessionState.Responding && !fromTool)
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

    private Task GiveImage(string path, PromptAttachment image, CancellationToken cancellation = default)
    {
        _ = new[] { image }.Prepare(string.Empty, engine);
        return Deliver(new Queued($"Here is the image {path} you asked to see.", [image], null, [imageNote]), true, cancellation);
    }

    /// <summary>
    /// Makes this session the helper of another, which cannot hand tasks on and works in the cache of the other.
    /// </summary>
    /// <param name="shared">The cache of the session the helper works for, which the helper does not clear when it ends.</param>
    internal void BecomeHelper(IStore shared)
    {
        lock (gate)
        {
            isHelper = true;
            cache = shared;
            ownsCache = false;
        }
    }

    private async Task<string> Delegate(string task, bool shareContext, CancellationToken cancellation)
    {
        await delegating.WaitAsync(cancellation);

        try
        {
            PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
            PromptSession helper;

            if (shareContext)
            {
                helper = await Branch(null, cancellation, false) as PromptSession ?? throw new InvalidOperationException("A fork of a session is a session of the same kind.");
            }
            else
            {
                PromptSessionRecord source;

                lock (gate)
                {
                    source = current;
                }

                helper = await provider.Create(new PromptProviderOptions { ApiKey = apiKey, Options = source.Options, WorkspacePath = source.WorkingFolder }, cancellation) as PromptSession ?? throw new InvalidOperationException("A session of the provider is a session of the same kind.");

                try
                {
                    snapshot.CopyTo(helper.settings);
                }
                catch
                {
                    await Discard(helper);
                    throw;
                }
            }

            helper.BecomeHelper(Cache);
            string asked;

            lock (gate)
            {
                asked = turnText ?? string.Empty;
            }

            try
            {
                string answer = await helper.Request(shareContext ? $"{sharedNote}\n\nThe request the other AI is working on now:\n{asked}\n\nYour task:\n{task}" : $"{delegatedNote}\n\n{task}", null, cancellation);

                lock (gate)
                {
                    usage = usage.Plus(helper.Usage);
                }

                return answer.Length > 0 ? answer : "(the helper gave no answer)";
            }
            finally
            {
                await Discard(helper);
            }
        }
        finally
        {
            delegating.Release();
        }
    }

    private async Task Discard(PromptSession helper)
    {
        await helper.DisposeAsync();

        try
        {
            await provider.Delete(helper.Id);
        }
        catch (Exception exception) when (exception is PromptSessionException or ArgumentException or IOException)
        {
        }
    }

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
            PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
            await Select(snapshot, running);
            Update(record => Merge(record, snapshot));
            await PersistConfiguration(running);

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

            return new TurnStart(effective, run.Schema is { } formatted ? engine.FormatSchema(formatted) : null, await Serve(snapshot, running), engine.CanPersist, given, told);
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

            IPromptProcess? before = live;
            IPromptProcess process = keep ? await Open(start.Record, start.Schema, start.Endpoint) : Launch(start.Record, start.Schema, start.Endpoint);
            bool reused = keep && before is not null && ReferenceEquals(before, process);
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

            PromptInput sent = files.Count > 0 ? prompting with { Attachments = [], Files = files } : prompting;
            IAsyncEnumerator<PromptEngineEvent> events = process.Run(sent, cancellation).GetAsyncEnumerator(cancellation);

            try
            {
                bool first = true;

                while (await events.MoveNextAsync())
                {
                    PromptEngineEvent output = events.Current;

                    if (first && reused && output.Kind == PromptEngineEventKind.Failed && !process.IsRunning && !cancellation.IsCancellationRequested)
                    {
                        await events.DisposeAsync();
                        process = await Open(start.Record, start.Schema, start.Endpoint);
                        lock (gate)
                        {
                            active = process;
                            workProcess = process;
                        }

                        events = process.Run(sent, cancellation).GetAsyncEnumerator(cancellation);
                        reused = false;
                        continue;
                    }

                    first = false;
                    yield return output;
                }
            }
            finally
            {
                await events.DisposeAsync();

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
        string folder = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
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
            string folder = source.WorkspacePath ?? store.GetWorkspaceFolder(id);
            Directory.CreateDirectory(folder);

            PromptSessionLink link = source.ToLink() with { Id = id, Created = Time.GetUtcNow(), WorkingFolder = folder, AiSessionId = branch.AiSessionId, ForkedFrom = branch.ForkedFrom, ForkedAt = branch.ForkedAt };
            IPromptSession fork;

            try
            {
                await store.Save(link, linked.Token);
                fork = opener.Open(link, provider, engine, apiKey);
            }
            catch
            {
                if (source.WorkspacePath is null && Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }

                throw;
            }

            if (fork is PromptSession inheriting)
            {
                lock (gate)
                {
                    inheriting.Inherit(instructionsKnown, knownInstructions);
                }
            }

            if (fork.Configuration is PromptSessionSettings target)
            {
                snapshot.CopyTo(target);
            }

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
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closing.Token);
        await turn.WaitAsync(linked.Token);

        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
            await Select(snapshot, linked.Token);
            Update(record => Merge(record, snapshot));

            PromptSessionRecord effective;

            lock (gate)
            {
                effective = current;
            }

            ToolEndpoint? endpoint = await Serve(snapshot, linked.Token);
            IPromptProcess process = await Open(effective, null, endpoint);
            active = process;

            try
            {
                return await operation(process, linked.Token);
            }
            finally
            {
                active = null;
            }
        }
        finally
        {
            turn.Release();
        }
    }

    private IReadOnlyDictionary<string, string> Describe(PromptSessionRecord record)
        => PromptDescriptors.Describe(engine.Identifier, record.Model, record.ModelSettings);

    private void SyncWorkspace(bool isSandboxed)
    {
        if (workspaceStore.IsSandboxed != isSandboxed)
        {
            workspaceStore = new DiskStore(workspaceStore.Folder, isSandboxed);
        }
    }

    private async Task<ToolEndpoint?> Serve(PromptSessionSettingsSnapshot snapshot, CancellationToken cancellation = default)
    {
        PromptTool[] exposed;
        bool hasShell;
        DiskStore workspace;
        PromptTool? images;
        PromptTool? fetch;
        PromptTool? search;
        PromptTool? generate;
        PromptTool? delegation;

        lock (gate)
        {
            SyncWorkspace(snapshot.IsSandboxed);
            workspace = workspaceStore;
            hasShell = snapshot.IsShellAllowed;
            IStore everything = Files;

            if (hasShell)
            {
                string folder = workspace.Folder;

                if (shellTool is null || shellFolder != folder)
                {
                    shellTool = libraryTools.CreateShell(folder);
                    shellFolder = folder;
                }
            }

            if (fileToolWorkspace != workspace)
            {
                fileTools = null;
                imageTool = null;
                generateTool = null;
                fileToolWorkspace = workspace;
            }

            PromptTool[] files = fileTools ??= libraryTools.Create(everything).ToArray();
            images = current.Model?.AcceptsImages != false && engine.Accepts("image/png") ? imageTool ??= libraryTools.CreateImage(everything, GiveImage) : null;
            fetch = snapshot.IsWebAllowed ? fetchTool ??= libraryTools.CreateFetch() : null;
            if (generateWith != snapshot.ImageGeneration)
            {
                generateTool = null;
                generateWith = snapshot.ImageGeneration;
                imageGenerator = snapshot.ImageGeneration is { } options ? generators(options) : null;
            }

            generate = imageGenerator is { } generator ? generateTool ??= libraryTools.CreateGenerateImage(everything, generator) : null;
            search = snapshot.IsWebAllowed && !engine.NativeSearch ? searchTool ??= libraryTools.CreateSearch() : null;
            timeTool ??= libraryTools.CreateTime();
            toolSearchTool ??= libraryTools.CreateToolSearch(() => servedTools);
            delegation = isHelper ? null : delegateTool ??= libraryTools.CreateDelegate(Delegate);
            exposed = files.Concat(hasShell ? [shellTool!] : []).Concat(images is null ? [] : [images]).Concat(fetch is null ? [] : [fetch]).Concat(search is null ? [] : [search]).Concat(generate is null ? [] : [generate]).Concat([timeTool, toolSearchTool]).Concat(delegation is null ? [] : [delegation]).Concat(snapshot.Tools).Concat(snapshot.Asker is null ? [] : [AskTool()]).Append(RespondTool()).ToArray();

            if (exposed.GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } clash)
            {
                throw new PromptSessionException($"The AI would be offered two tools named '{clash.Key}'.");
            }
        }

        cancellation.ThrowIfCancellationRequested();

        if (toolServer is null || !exposed.SequenceEqual(servedTools))
        {
            await StopTools();
            toolServer = toolHost.Start(exposed);
            servedTools = exposed;
            log.ToolServerStarted(Id, exposed.Length);
        }

        return toolServer.Endpoint with { HasShell = hasShell, CacheName = libraryTools.CacheName };
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
                if (!engine.CanPersist)
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
            await ClearCache();
            await SaveConfiguration();
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

    private async Task SaveConfiguration()
    {
        PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
        bool changed;

        lock (gate)
        {
            current = Merge(current, snapshot);
            changed = !current.SameStored(persisted);
        }

        try
        {
            if (changed && await store.Find(current.Id) is not null)
            {
                await Persist();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task ClearCache()
    {
        IStore owned;

        lock (gate)
        {
            if (!ownsCache)
            {
                return;
            }

            owned = cache;
        }

        foreach (StoreEntry entry in await owned.List(string.Empty).ToListAsync())
        {
            await owned.Delete(entry.Path);
        }
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
        => record with { Capability = snapshot.Capability, Effort = snapshot.Effort, IsShellAllowed = snapshot.IsShellAllowed, IsSandboxed = snapshot.IsSandboxed, IsWebAllowed = snapshot.IsWebAllowed, ImageGeneration = snapshot.ImageGeneration, Instructions = snapshot.Instructions, NativeSearch = snapshot.IsWebAllowed && engine.NativeSearch, McpServers = new Dictionary<string, PromptMcpServer>(snapshot.McpServers) };

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

    private async Task PersistConfiguration(CancellationToken cancellation)
    {
        bool changed;

        lock (gate)
        {
            changed = !current.SameStored(persisted);
        }

        if (changed)
        {
            await Persist(cancellation);
        }
    }

    private async Task Select(PromptSessionSettingsSnapshot snapshot, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (selected == (snapshot.Capability, snapshot.Effort))
            {
                return;
            }
        }

        (PromptModel model, IReadOnlyDictionary<string, string> chosen) = await provider.Select(snapshot.Capability, snapshot.Effort, apiKey, cancellation);
        IReadOnlyDictionary<string, string> changed;
        bool different;

        lock (gate)
        {
            selected = (snapshot.Capability, snapshot.Effort);
            current = current with { Model = model, ModelSettings = chosen };
            changed = Describe(current);
            different = !changed.OrderBy(entry => entry.Key, StringComparer.Ordinal).SequenceEqual(descriptors.OrderBy(entry => entry.Key, StringComparer.Ordinal));
            descriptors = changed;
        }

        if (different)
        {
            output.Publish(new PromptDescriptorsEvent { Descriptors = changed });
        }

        log.ModelChanged(Id, $"{engine.Identifier} runs {model.Id} for {snapshot.Capability} capability and {snapshot.Effort} effort");
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
