namespace Markwardt.LiveAi.Testing;

/// <summary>
/// An <see cref="IPromptProvider" /> for tests that makes <see cref="FakePromptSession" />s and keeps them in memory, so code that creates, loads, deletes and tests sessions can be tried without a command line tool or a data folder.
/// </summary>
public sealed class FakePromptProvider : IPromptProvider
{
    private readonly object gate = new();

    private readonly List<FakePromptSession> sessions = [];

    private readonly List<string?> tests = [];

    private readonly Dictionary<string, (DateTimeOffset Created, string? WorkspacePath)> made = [];

    private readonly List<string?> listings = [];

    private PromptTestResult testResult = new() { IsAvailable = true };

    /// <summary>
    /// Gets or sets the models <see cref="GetModels" /> lists, which every session this provider makes starts with the first of. The default is one fake model, <c>fake-model</c>, that offers an <c>effort</c> setting (<c>low</c>, <c>medium</c>, <c>high</c>). Give an empty list for a tool that offers none.
    /// </summary>
    public IReadOnlyList<PromptModel> Models { get; set; } = [new PromptModel { Id = "fake-model", Name = "Fake model", AcceptsImages = true, CallsTools = true, Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"])] }];

    /// <summary>
    /// Gets the API keys <see cref="GetModels" /> was asked with, in order, with <see langword="null" /> for the configured sign in.
    /// </summary>
    public IReadOnlyList<string?> Listings
    {
        get
        {
            lock (gate)
            {
                return [.. listings];
            }
        }
    }

    /// <summary>
    /// Gets or sets a function that is called with every session this provider makes, to script its answers before the code under test gets it.
    /// </summary>
    public Action<FakePromptSession>? OnCreate { get; set; }

    /// <summary>
    /// Gets or sets the clock the sessions of this provider run on, or <see langword="null" /> for the system's. Set it before creating sessions.
    /// </summary>
    public TimeProvider? Time { get; set; }

    /// <summary>
    /// Gets or sets the limit windows that <see cref="GetWindows" /> returns and the sessions of this provider read, unless a session was given one of its own. With none, reading the status fails the way a login without limits does.
    /// </summary>
    public IReadOnlyList<PromptWindow>? Windows { get; set; }

    /// <summary>
    /// Gets or sets what <see cref="Test" /> returns, which is an available tool until a test sets another result.
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
    /// Gets the API keys that <see cref="Test" /> was called with, in order (<see langword="null" /> for none).
    /// </summary>
    public IReadOnlyList<string?> Tests
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
    /// Gets the sessions that were made and not deleted, in the order they were made.
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
    /// Makes <see cref="TestResult" /> report that the tool is not available.
    /// </summary>
    /// <param name="error">Why it is not available.</param>
    /// <returns>This provider, for chaining.</returns>
    public FakePromptProvider FailTests(string error)
    {
        TestResult = new PromptTestResult { IsAvailable = false, Error = error };
        return this;
    }

    /// <inheritdoc />
    public Task<IPromptSession> Create(PromptSessionOptions? options = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string? workspacePath = options?.WorkspacePath;
        bool sandbox = options?.Sandbox ?? true;
        FakePromptSession session = new(this, options?.ApiKey, () => Windows, Time, options?.Model ?? Models.FirstOrDefault());

        lock (gate)
        {
            sessions.Add(session);
            made[session.Id] = ((Time ?? TimeProvider.System).GetUtcNow(), workspacePath is null ? null : Path.GetFullPath(workspacePath));
        }

        if (workspacePath is not null)
        {
            session.Configuration.Workspace = IStore.FromDisk(workspacePath, sandbox);
        }

        OnCreate?.Invoke(session);
        return Task.FromResult<IPromptSession>(session);
    }

    /// <inheritdoc />
    /// <remarks>A session counts as started once it was given a prompt.</remarks>
    public Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PromptSessionInfo>>([.. sessions
                .Select(session => new PromptSessionInfo { Id = session.Id, Created = made[session.Id].Created, WorkspacePath = made[session.Id].WorkspacePath, IsStarted = session.Prompts.Count > 0 })
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
    /// <remarks>A session that is still open is returned as it is; one that was disposed is brought back with its configuration, answers and conversation, as a real session is when it is loaded.</remarks>
    public Task<IPromptSession> Load(string id, PromptSessionOptions? options = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        if (options?.WorkspacePath is not null)
        {
            throw new ArgumentException("A loaded session keeps the workspace it was stored with; use AttachTo to change it.", nameof(options));
        }

        string? apiKey = options?.ApiKey;

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

            if (options?.Model is { } model)
            {
                sessions[index].Configuration.Model = model;
            }

            return Task.FromResult<IPromptSession>(sessions[index]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptModel>> GetModels(string? apiKey = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        lock (gate)
        {
            listings.Add(apiKey);
        }

        return Task.FromResult(Models);
    }

    /// <inheritdoc />
    public async Task<PromptModel> GetModel(string id, string? apiKey = null, CancellationToken cancellation = default)
        => (await GetModels(apiKey, cancellation)).FirstOrDefault(model => model.Id == id) ?? throw new PromptSessionModelException($"The fake provider does not offer a model '{id}'.");

    /// <inheritdoc />
    public Task<PromptTestResult> Test(string? apiKey = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        lock (gate)
        {
            tests.Add(apiKey);
            return Task.FromResult(testResult);
        }
    }

    /// <inheritdoc />
    /// <exception cref="PromptSessionException">No <see cref="Windows" /> were set, which is how a login without limits looks.</exception>
    public Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Windows is { } windows ? Task.FromResult(windows) : Task.FromException<IReadOnlyList<PromptWindow>>(new PromptSessionException("The fake provider reported no subscription limits."));
    }
}
