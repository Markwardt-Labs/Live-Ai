namespace Markwardt.LiveAi.Testing;

/// <summary>
/// An <see cref="IPrompter" /> for tests that makes <see cref="FakePromptSession" />s and keeps them in memory, so code that creates, loads, lists, deletes and tests sessions can be tried without a command line tool or a data folder. It makes the same kind of session whatever the options are for, and records them.
/// </summary>
public sealed class FakePrompter : IPrompter
{
    private readonly object gate = new();

    private readonly List<FakePromptSession> sessions = [];

    private readonly List<PromptSessionOptions> tests = [];

    private readonly List<PromptSessionOptions> created = [];

    private readonly Dictionary<string, (DateTimeOffset Created, string Provider, string WorkspacePath)> made = [];

    private PromptTestResult testResult = new() { IsAvailable = true };

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
    /// Gets the options <see cref="Test" /> was asked with, in order.
    /// </summary>
    public IReadOnlyList<PromptSessionOptions> Tests
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
    /// Gets the options <see cref="Create" /> was asked with, in order.
    /// </summary>
    public IReadOnlyList<PromptSessionOptions> Created
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
    public Task<IPromptSession> Create(PromptSessionOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellation.ThrowIfCancellationRequested();
        (string provider, string? apiKey) = options switch
        {
            ClaudeSessionOptions claude => ("Claude Code", claude.ApiKey),
            CodexSessionOptions codex => ("Codex", codex.ApiKey),
            OllamaSessionOptions ollama => ("OpenCode", (string?)null),
            _ => throw new ArgumentException($"{options.GetType().Name} is not a kind of session options the library knows.", nameof(options))
        };

        FakePromptSession session = new(apiKey, Time);
        session.UseWorkspace(options.WorkspacePath is { } path ? Path.GetFullPath(path) : null);
        string workspacePath = session.WorkspacePath;

        lock (gate)
        {
            created.Add(options);
            sessions.Add(session);
            made[session.Id] = ((Time ?? TimeProvider.System).GetUtcNow(), provider, workspacePath);
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
                .Select(session => new PromptSessionInfo { Id = session.Id, Provider = made[session.Id].Provider, Created = made[session.Id].Created, WorkspacePath = made[session.Id].WorkspacePath, IsStarted = session.Prompts.Count > 0 })
                .OrderByDescending(info => info.Created)]);
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

            return Task.FromResult<IPromptSession>(sessions[index]);
        }
    }

    /// <inheritdoc />
    public Task<PromptTestResult> Test(PromptSessionOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellation.ThrowIfCancellationRequested();

        lock (gate)
        {
            tests.Add(options);
            return Task.FromResult(testResult);
        }
    }
}
