namespace Markwardt.LiveAi.Testing;

/// <summary>
/// An <see cref="IPrompter" /> for tests that makes <see cref="FakePromptSession" />s and keeps them in memory, so code that creates, loads, lists, deletes and tests sessions can be tried without a command line tool or a data folder. It makes the same kind of session whatever the setup is for, and records it.
/// </summary>
public sealed class FakePrompter : IPrompter
{
    private readonly object gate = new();

    private readonly List<FakePromptSession> sessions = [];

    private readonly List<PromptSetup> tests = [];

    private readonly List<PromptSetup> created = [];

    private readonly Dictionary<string, (DateTimeOffset LastLoaded, string WorkspacePath)> made = [];

    private PromptTestResult testResult = new() { IsAvailable = true };

    /// <inheritdoc />
    public PromptEnvironment Environment
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = new();

    /// <summary>
    /// Gets or sets an action that is called with every session this prompter makes, before it is returned, to script its answers.
    /// </summary>
    public Action<FakePromptSession>? OnCreate { get; set; }

    /// <summary>
    /// Gets or sets the clock the sessions run on and that dates them, or <see langword="null" /> for the system's. Give a <see cref="TimeProvider" /> that the test controls to test code that waits without waiting.
    /// </summary>
    public TimeProvider? Time { get; set; }

    /// <summary>
    /// Gets or sets what <see cref="Test" /> returns: the tool is available by default.
    /// </summary>
    public PromptTestResult TestResult
    {
        get
        {
            lock (gate)
            {
                return testResult;
            }
        }

        set
        {
            lock (gate)
            {
                testResult = value;
            }
        }
    }

    /// <summary>
    /// Gets the setups <see cref="Test" /> was asked with, in order.
    /// </summary>
    public IReadOnlyList<PromptSetup> Tests
    {
        get
        {
            lock (gate)
            {
                return [.. tests];
            }
        }
    }

    /// <summary>
    /// Gets the setups <see cref="Create" /> was asked with, in order.
    /// </summary>
    public IReadOnlyList<PromptSetup> Created
    {
        get
        {
            lock (gate)
            {
                return [.. created];
            }
        }
    }

    /// <summary>
    /// Gets the sessions this prompter made and has not deleted, in the order they were made.
    /// </summary>
    public IReadOnlyList<FakePromptSession> Sessions
    {
        get
        {
            lock (gate)
            {
                return [.. sessions];
            }
        }
    }

    /// <summary>
    /// Makes <see cref="Test" /> report that the tool is not available.
    /// </summary>
    /// <param name="error">The reason.</param>
    /// <returns>This prompter, for chaining.</returns>
    public FakePrompter FailTests(string error)
    {
        TestResult = new PromptTestResult { IsAvailable = false, Error = error };
        return this;
    }

    /// <inheritdoc />
    public Task<IPromptSession> Create(PromptSetup setup, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        cancellation.ThrowIfCancellationRequested();
        (PromptProvider provider, string? apiKey) = setup switch
        {
            ClaudeSetup claude => (PromptProvider.Claude, claude.ApiKey ?? Environment.ClaudeApiKey),
            CodexSetup codex => (PromptProvider.Codex, codex.ApiKey ?? Environment.CodexApiKey),
            LocalSetup => (PromptProvider.Local, (string?)null),
            _ => throw new ArgumentException($"{setup.GetType().Name} is not a kind of session setup the library knows.", nameof(setup))
        };

        FakePromptSession session = new(apiKey, Time, provider);
        session.UseWorkspace(setup.WorkspacePath is { } path ? Path.GetFullPath(path) : null);
        session.Configuration.Capability = Environment.Capability;
        session.Configuration.Effort = Environment.Effort;
        session.Configuration.IsShellAllowed = Environment.IsShellAllowed;
        session.Configuration.IsSystemAccessAllowed = Environment.IsSystemAccessAllowed;
        session.Configuration.IsWebAllowed = Environment.IsWebAllowed;
        session.Configuration.IsImageGenerationAllowed = Environment.IsImageGenerationAllowed;
        session.Configuration.Instructions = Environment.Instructions;
        string workspacePath = session.WorkspacePath;

        lock (gate)
        {
            created.Add(setup);
            sessions.Add(session);
            made[session.Id] = ((Time ?? TimeProvider.System).GetUtcNow(), workspacePath);
        }

        OnCreate?.Invoke(session);
        return Task.FromResult<IPromptSession>(session);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PromptSessionInfo>>([.. sessions
                .Select(session => new PromptSessionInfo { Id = session.Id, Provider = session.Provider, LastLoaded = made[session.Id].LastLoaded, WorkspacePath = session.WorkspacePath })
                .OrderByDescending(info => info.LastLoaded)]);
        }
    }

    /// <inheritdoc />
    public Task<bool> Delete(string id, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            made.Remove(id);
            return Task.FromResult(sessions.RemoveAll(session => session.Id == id) > 0);
        }
    }

    /// <inheritdoc />
    public async Task Move(string id, string workspacePath, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        DateTimeOffset? before = null;

        lock (gate)
        {
            if (made.TryGetValue(id, out (DateTimeOffset LastLoaded, string WorkspacePath) known))
            {
                before = known.LastLoaded;
            }
        }

        IPromptSession session = await Load(id, cancellation);
        await session.Move(workspacePath, cancellation);
        await session.DisposeAsync();

        lock (gate)
        {
            if (before is { } time && made.TryGetValue(id, out (DateTimeOffset LastLoaded, string WorkspacePath) current))
            {
                made[id] = (time, current.WorkspacePath);
            }
        }
    }

    /// <inheritdoc />
    public Task<IPromptSession> Load(string id, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        lock (gate)
        {
            int index = sessions.FindIndex(session => session.Id == id);

            if (index < 0)
            {
                return Task.FromException<IPromptSession>(new PromptSessionNotFoundException(id));
            }

            if (sessions[index].IsDisposed)
            {
                sessions[index] = sessions[index].Resume();
            }

            made[id] = ((Time ?? TimeProvider.System).GetUtcNow(), made[id].WorkspacePath);

            return Task.FromResult<IPromptSession>(sessions[index]);
        }
    }

    /// <inheritdoc />
    public Task<PromptTestResult> Test(PromptSetup setup, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        cancellation.ThrowIfCancellationRequested();

        lock (gate)
        {
            tests.Add(setup);
            return Task.FromResult(testResult);
        }
    }
}
