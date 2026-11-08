namespace Markwardt.LiveAi;

/// <summary>
/// Creates, loads, deletes and tests the sessions of one command line tool, and chooses the model the sessions run on.
/// </summary>
internal interface IPromptToolService
{
    /// <summary>
    /// Creates a session and stores it under a new identifier. The tool's own session starts with the first prompt.
    /// </summary>
    /// <param name="options">The API key and the workspace, or <see langword="null" /> for the configured sign in and a workspace folder the library makes.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The new session, which the caller disposes.</returns>
    /// <exception cref="DirectoryNotFoundException">The workspace folder does not exist.</exception>
    Task<IPromptSession> Create(PromptToolServiceOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Loads a stored session so that it continues the same conversation.
    /// </summary>
    /// <param name="id">The identifier of the session.</param>
    /// <param name="options">The API key, or <see langword="null" /> for the configured sign in.</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <returns>The session, which the caller disposes.</returns>
    /// <exception cref="ArgumentException">The session was made with another tool, or a workspace path is given.</exception>
    /// <exception cref="PromptSessionNotFoundException">There is no such session, or the tool no longer has its conversation.</exception>
    Task<IPromptSession> Load(string id, PromptToolServiceOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Lists the stored sessions of the service's tool, the most recently loaded first.
    /// </summary>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The sessions.</returns>
    Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default);

    /// <summary>
    /// Deletes a stored session for good: the link, the tool's conversation and the workspace folder the library made for it.
    /// </summary>
    /// <param name="id">The identifier of the session.</param>
    /// <param name="cancellation">Cancels the deletion.</param>
    /// <returns><see langword="true" /> when the session was there and is gone.</returns>
    /// <exception cref="ArgumentException">The session was made with another tool.</exception>
    Task<bool> Delete(string id, CancellationToken cancellation = default);

    /// <summary>
    /// Moves a stored session to another workspace folder without loading it, bringing the tool's conversation along where the tool keeps it by folder.
    /// </summary>
    /// <param name="id">The identifier of the session.</param>
    /// <param name="workspacePath">The existing folder that becomes the session's workspace.</param>
    /// <param name="cancellation">Cancels the move.</param>
    /// <exception cref="PromptSessionNotFoundException">There is no such session.</exception>
    /// <exception cref="ArgumentException">The session was made with another tool.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="PromptSessionException">The conversation could not be brought to the folder.</exception>
    Task Move(string id, string workspacePath, CancellationToken cancellation = default);

    /// <summary>
    /// Finds out whether the tool is usable, and how much of the subscription is used where the tool says.
    /// </summary>
    /// <param name="apiKey">The API key to try, or <see langword="null" /> for the configured sign in.</param>
    /// <param name="cancellation">Cancels the test.</param>
    /// <returns>The result, which says why when the tool is not usable.</returns>
    Task<PromptTestResult> Test(string? apiKey = null, CancellationToken cancellation = default);

    /// <summary>
    /// Chooses the model that fits a capability among the ones the tool offers, and the settings that fit an effort.
    /// </summary>
    /// <param name="capability">How capable the model should be.</param>
    /// <param name="effort">How much effort the model should put into each prompt.</param>
    /// <param name="apiKey">The API key the tool is used with, or <see langword="null" /> for the configured sign in.</param>
    /// <param name="cancellation">Cancels the choice.</param>
    /// <returns>The model and its settings.</returns>
    /// <exception cref="PromptSessionException">The tool offers no usable model.</exception>
    Task<(PromptModel Model, IReadOnlyDictionary<string, string> Settings)> Select(PromptCapability capability, PromptEffort effort, string? apiKey = null, CancellationToken cancellation = default);
}

/// <inheritdoc />
internal sealed class PromptToolService : IPromptToolService
{
    private readonly IPromptEngine engine;

    private readonly IPromptSessionStore store;

    private readonly IPromptSessionOpener opener;

    private readonly ITurnRunner runner;

    private readonly TimeProvider time;


    private readonly Dictionary<string, (DateTimeOffset Listed, IReadOnlyList<PromptModel> Models)> listed = [];

    private readonly SemaphoreSlim listing = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptToolService" /> class.
    /// </summary>
    /// <param name="engine">The tool the sessions run on.</param>
    /// <param name="store">Keeps the stored sessions.</param>
    /// <param name="opener">Makes the session objects.</param>
    /// <param name="runner">Runs the engine's processes.</param>
    /// <param name="time">The clock that dates the sessions and ages the list of models, or <see langword="null" /> for the system's.</param>
    public PromptToolService(IPromptEngine engine, IPromptSessionStore store, IPromptSessionOpener opener, ITurnRunner runner, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        this.engine = engine;
        this.store = store;
        this.opener = opener;
        this.runner = runner;
    }

    /// <summary>
    /// Gets or sets how long a list of models is kept.
    /// </summary>
    internal TimeSpan ModelsLifetime { get; init; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task<IPromptSession> Create(PromptToolServiceOptions? options = null, CancellationToken cancellation = default)
    {
        string? apiKey = options?.ApiKey;
        string? workspacePath = options?.WorkspacePath;
        Guid id = Guid.NewGuid();
        string? workspace = workspacePath is null ? null : Path.GetFullPath(workspacePath);
        string folder = store.GetWorkspaceFolder(id);

        if (workspace is null)
        {
            Directory.CreateDirectory(folder);
        }
        else if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"The workspace folder '{workspacePath}' does not exist.");
        }

        PromptSessionLink link = new() { Id = id, LastLoaded = time.GetUtcNow(), Provider = engine.Provider, WorkingFolder = workspace ?? folder, WorkspacePath = workspace, Setup = options?.Setup };

        if (options?.Environment is { } environment)
        {
            link = link with { Capability = environment.Capability, Effort = environment.Effort, IsShellAllowed = environment.IsShellAllowed, IsSystemAccessAllowed = environment.IsSystemAccessAllowed, IsWebAllowed = environment.IsWebAllowed, IsImageGenerationAllowed = environment.IsImageGenerationAllowed, Instructions = environment.Instructions, Storage = environment.Storage };
        }

        try
        {
            await store.Save(link, cancellation);
            return opener.Open(link, this, engine, apiKey);
        }
        catch
        {
            if (workspace is null && Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IPromptSession> Load(string id, PromptToolServiceOptions? options = null, CancellationToken cancellation = default)
    {
        if (options?.WorkspacePath is not null)
        {
            throw new ArgumentException("A loaded session keeps the workspace it was stored with; use Move to change it.", nameof(options));
        }

        PromptSessionLink record = Guid.TryParse(id, out Guid guid) && await store.Find(guid, cancellation) is { } found ? found : throw new PromptSessionNotFoundException(id);
        Check(record, id);

        if ((record.AiSessionId ?? record.ForkedFrom) is { } origin && !engine.HasSession(origin))
        {
            throw new PromptSessionNotFoundException(id, $"The {engine.Identifier} session of session {id} no longer exists. If the tool was given another configuration folder than the one that made the session, load it with that folder; otherwise delete the session.");
        }

        if (record.WorkspacePath is null)
        {
            Directory.CreateDirectory(record.WorkingFolder);
        }

        record = record with { LastLoaded = time.GetUtcNow() };
        IPromptSession session = opener.Open(record, this, engine, options?.ApiKey);

        try
        {
            await store.Save(record, cancellation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return session;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default)
        => [.. (await store.List(cancellation))
            .Where(link => link.Provider == engine.Provider)
            .Select(link => new PromptSessionInfo { Id = link.Id.ToString("N"), Provider = engine.Provider, LastLoaded = link.LastLoaded, WorkspacePath = link.WorkingFolder })];

    /// <inheritdoc />
    public async Task<bool> Delete(string id, CancellationToken cancellation = default)
    {
        if (!Guid.TryParse(id, out Guid guid) || await store.Find(guid, cancellation) is not { } record)
        {
            return false;
        }

        Check(record, id);

        try
        {
            if (record.AiSessionId is not null)
            {
                await engine.DeleteSession(record.AiSessionId, cancellation);
            }

            if (record.WorkspacePath is null && record.WorkingFolder == store.GetWorkspaceFolder(guid) && Directory.Exists(record.WorkingFolder))
            {
                Directory.Delete(record.WorkingFolder, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PromptSessionException($"The session {id} could not be deleted: {exception.Message}", exception);
        }

        await store.Delete(guid);
        return true;
    }

    /// <inheritdoc />
    public async Task Move(string id, string workspacePath, CancellationToken cancellation = default)
    {
        PromptSessionLink record = Guid.TryParse(id, out Guid guid) && await store.Find(guid, cancellation) is { } found ? found : throw new PromptSessionNotFoundException(id);
        Check(record, id);
        string workspace = Path.GetFullPath(workspacePath);

        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"The workspace folder '{workspacePath}' does not exist.");
        }

        if ((record.AiSessionId ?? record.ForkedFrom) is { } origin)
        {
            await engine.MoveSession(origin, workspace, cancellation);
        }

        await store.Save(record with { WorkingFolder = workspace, WorkspacePath = workspace }, cancellation);

        if (record.WorkspacePath is null && record.WorkingFolder != workspace && record.WorkingFolder == store.GetWorkspaceFolder(guid) && Directory.Exists(record.WorkingFolder))
        {
            try
            {
                Directory.Delete(record.WorkingFolder, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <inheritdoc />
    public async Task<PromptTestResult> Test(string? apiKey = null, CancellationToken cancellation = default)
    {
        try
        {
            await foreach (PromptEngineEvent output in runner.Run(engine, await engine.CreateTestStart(apiKey, cancellation), "Reply with the digit 1.", cancellation))
            {
                switch (output.Kind)
                {
                    case PromptEngineEventKind.Failed:
                        return Unavailable(output.Text);
                    case PromptEngineEventKind.Finished:
                        return new PromptTestResult { IsAvailable = true, Windows = await Windows(cancellation) };
                }
            }
        }
        catch (PromptSessionException exception)
        {
            return Unavailable(exception.Message);
        }

        return Unavailable($"{engine.Identifier} ended without answering.");
    }

    /// <inheritdoc />
    public async Task<(PromptModel Model, IReadOnlyDictionary<string, string> Settings)> Select(PromptCapability capability, PromptEffort effort, string? apiKey = null, CancellationToken cancellation = default)
    {
        IReadOnlyList<PromptModel> models = (await Models(apiKey, false, cancellation)).Models;

        if (models.Count == 0)
        {
            models = engine.FallbackModels ?? [];
        }

        if (models.Count == 0)
        {
            throw new PromptSessionException($"{engine.Identifier} offers no model. Check that it is installed and signed in.");
        }

        PromptModel model = models.ChooseFor(capability, engine.PreferredModels?.For(capability));
        return (model, model.SettingsFor(effort, engine.ModelOverrides(capability)));
    }

    private async Task<(IReadOnlyList<PromptModel> Models, bool Cached)> Models(string? apiKey, bool fresh, CancellationToken cancellation)
    {
        string key = apiKey is null ? string.Empty : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        await listing.WaitAsync(cancellation);

        try
        {
            DateTimeOffset now = time.GetUtcNow();

            if (!fresh && listed.TryGetValue(key, out (DateTimeOffset Listed, IReadOnlyList<PromptModel> Models) known) && now - known.Listed < ModelsLifetime)
            {
                return (known.Models, true);
            }

            IReadOnlyList<PromptModel> models = await engine.GetModels(apiKey, cancellation);

            if (models.Count > 0)
            {
                listed[key] = (now, models);
            }
            else
            {
                listed.Remove(key);
            }

            return (models, false);
        }
        finally
        {
            listing.Release();
        }
    }

    private async Task<IReadOnlyList<PromptWindow>?> Windows(CancellationToken cancellation)
    {
        try
        {
            return await engine.GetWindows(cancellation);
        }
        catch (PromptSessionException)
        {
            return null;
        }
    }

    private PromptTestResult Unavailable(string error)
        => new() { IsAvailable = false, Error = error };

    private void Check(PromptSessionLink record, string id)
    {
        if (record.Provider != engine.Provider)
        {
            throw new ArgumentException($"The session {id} was made with {record.Provider}, not with {engine.Provider}.", nameof(id));
        }
    }
}
