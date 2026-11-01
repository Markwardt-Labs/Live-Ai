namespace Markwardt.LiveAi;

/// <summary>
/// Creates, resumes, deletes and checks the AI sessions of one command line tool (its <see cref="IPromptEngine" />), which are stored in a folder so that they outlive the process that created them and can be resumed by another one.
/// </summary>
public interface IPromptProvider
{
    /// <summary>
    /// Creates a session and stores it under a new identifier. The tool's own session starts with the first prompt. The session starts with balanced capability and medium reasoning; change them with <see cref="IPromptSessionConfiguration.Capability" /> and <see cref="IPromptSessionConfiguration.Reasoning" />, also before the first prompt.
    /// </summary>
    /// <param name="options">The API key and the workspace (<see cref="PromptSessionOptions.WorkspacePath" /> and <see cref="PromptSessionOptions.Sandbox" />), or <see langword="null" /> for the configured subscription and no workspace. The workspace path is stored with the session: the session's <see cref="IPromptSessionConfiguration.Workspace" /> is set to <c>IStore.FromDisk</c> of it, now and every time the session is loaded, and the tool runs in it instead of in an empty folder of its own. A session that is deleted never deletes the folder. It is the only workspace that is stored: assigning <see cref="IPromptSessionConfiguration.Workspace" /> later changes the running session only, and a load starts with this path again (use <see cref="IPromptSession.AttachTo" /> to change it). The API key is never stored.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The new session.</returns>
    /// <exception cref="DirectoryNotFoundException">The workspace path is not an existing folder.</exception>
    Task<IPromptSession> Create(PromptSessionOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Resumes the session stored under an identifier: only the link to the tool's conversation is stored, so the session starts with the default settings (balanced capability, medium reasoning, nothing else) and the caller sets again whatever it needs through <see cref="IPromptSession.Configuration" /> before the next prompt. Its next prompt continues the tool's session, so the AI still has its context. When the tool's session has been deleted the load fails; the stored session is kept, since the provider may only be a wrong one (another configuration folder), and <see cref="Delete" /> removes it.
    /// </summary>
    /// <param name="id">The identifier of the session, from <see cref="IPromptSession.Id" />.</param>
    /// <param name="options">The API key to run with, or <see langword="null" /> to use the configured subscription (a key is never stored, so it is given again on every load). The workspace is stored with the session, so giving a workspace path here is refused.</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <returns>The session.</returns>
    /// <exception cref="PromptSessionNotFoundException"><paramref name="id" /> is not a session identifier, no session is stored under it, or the tool's session it was linked to is not there.</exception>
    /// <exception cref="ArgumentException">The session was made by a provider of another tool, or a workspace path is given.</exception>
    /// <exception cref="PromptSessionException">The stored session is unreadable.</exception>
    Task<IPromptSession> Load(string id, PromptSessionOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Lists the sessions of this provider's tool that are stored in its data folder, newest first, so a caller does not have to keep the identifiers itself. Sessions of another tool, and links that cannot be read, are left out.
    /// </summary>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>What is known about each stored session.</returns>
    Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default);

    /// <summary>
    /// Deletes a stored session for good: the link to the tool's conversation, the conversation itself, and the empty folder made for the tool to run in, so nothing is left to clean up. A session that is open should be disposed first. The folders that were mounted to the session, and a workspace folder, are not touched.
    /// </summary>
    /// <param name="id">The identifier of the session, from <see cref="IPromptSession.Id" />.</param>
    /// <param name="cancellation">Cancels the deletion.</param>
    /// <returns><see langword="true" /> if the session existed, and <see langword="false" /> if the identifier is not one of a stored session.</returns>
    /// <exception cref="ArgumentException">The session was made by a provider of another tool.</exception>
    /// <exception cref="PromptSessionException">The conversation or the folder could not be deleted; the link is kept, so the deletion can be tried again.</exception>
    Task<bool> Delete(string id, CancellationToken cancellation = default);

    /// <summary>
    /// Finds out whether the tool is actually usable by running it with the smallest prompt that will do, on the cheapest model at low effort, without tools and with nothing stored. That proves what looking for the command cannot: the tool is installed, signed in (or the key works) and reachable. A tool that does not work is a result, not an exception. The test ends as soon as the tool has answered (the tool is stopped then, even if it would go on with housekeeping), and it fails only on what the tool reports or when the tool ends without answering. It has no time limit of its own: a tool that is slow, a local model that has to load for example, is waited for, and a caller that wants a bound passes a cancellation.
    /// </summary>
    /// <param name="apiKey">The API key to try, or <see langword="null" /> to try the configured sign in.</param>
    /// <param name="cancellation">Cancels the test, which then throws.</param>
    /// <returns>Whether the tool is available and, when it is not, why.</returns>
    Task<PromptTestResult> Test(string? apiKey = null, CancellationToken cancellation = default);

    /// <summary>
    /// Reads how much of each window of the subscription's limits the tool has used, without sending a prompt. It takes a second or two and costs nothing. A tool that has no subscription limits to report, such as OpenCode, returns an empty list.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The limit windows the tool reports: a session and a week for Claude Code (and a week for each model its plan limits), one or two for Codex, none for a tool without subscription limits.</returns>
    /// <exception cref="PromptSessionException">The tool could not be started, failed, did not answer in time, or did not report limits (it runs on an API key).</exception>
    Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default);
}

/// <inheritdoc />
public sealed class PromptProvider : IPromptProvider
{
    private readonly IPromptEngine engine;

    private readonly IPromptSessionStore store;

    private readonly IPromptSessionOpener opener;

    private readonly ITurnRunner runner;

    private readonly TimeProvider time;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptProvider" /> class.
    /// </summary>
    /// <param name="engine">The tool the sessions run on: a <see cref="ClaudeEngine" />, a <see cref="CodexEngine" />, an <see cref="OpenCodeEngine" />, or one of your own (<see cref="IPromptEngine" />).</param>
    /// <param name="dataFolder">The folder that holds the stored sessions, or <see langword="null" /> for <c>Markwardt.LiveAi</c> in the local application data folder. A session can only be loaded by a provider that uses the same folder as the one that created it.</param>
    /// <param name="logger">Where the library reports what its sessions do (state changes, processes and tool servers started, input given to running work, retries, limits reached and failures, at debug level for the routine and at warning level for failures), or <see langword="null" /> to report nothing. Prompts, answers and files are never logged.</param>
    /// <param name="time">The clock the timeouts, retries and schedules of the sessions and their loops run on, or <see langword="null" /> for the system's. Give a <see cref="TimeProvider" /> that you control to test code that waits.</param>
    public PromptProvider(IPromptEngine engine, string? dataFolder = null, ILogger? logger = null, TimeProvider? time = null)
        : this(engine, new PromptSessionStore(dataFolder), new TurnRunner(), logger, time ?? TimeProvider.System)
    {
    }

    private PromptProvider(IPromptEngine engine, IPromptSessionStore store, ITurnRunner runner, ILogger? logger, TimeProvider time)
        : this(engine, store, new PromptSessionOpener(store, runner, new PromptToolHost(), new PromptLibraryTools(), logger, time), runner, time)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptProvider" /> class from the parts it works with.
    /// </summary>
    /// <param name="engine">The tool the sessions run on.</param>
    /// <param name="store">Keeps the stored sessions.</param>
    /// <param name="opener">Makes the session objects.</param>
    /// <param name="runner">Runs the engine's processes.</param>
    /// <param name="time">The clock that dates the sessions, or <see langword="null" /> for the system's.</param>
    internal PromptProvider(IPromptEngine engine, IPromptSessionStore store, IPromptSessionOpener opener, ITurnRunner runner, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        this.engine = engine;
        this.store = store;
        this.opener = opener;
        this.runner = runner;
    }

    /// <inheritdoc />
    public async Task<IPromptSession> Create(PromptSessionOptions? options = null, CancellationToken cancellation = default)
    {
        string? apiKey = options?.ApiKey;
        string? workspacePath = options?.WorkspacePath;
        bool sandbox = options?.Sandbox ?? true;
        Guid id = Guid.NewGuid();
        string? workspace = workspacePath is null ? null : Path.GetFullPath(workspacePath);
        string folder = Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id:N}");

        if (workspace is null)
        {
            Directory.CreateDirectory(folder);
        }
        else if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"The workspace folder '{workspacePath}' does not exist.");
        }

        PromptSessionLink link = new() { Id = id, Created = time.GetUtcNow(), Provider = engine.Identifier, WorkingFolder = workspace ?? folder, WorkspacePath = workspace, Sandbox = sandbox };

        try
        {
            await store.Save(link, cancellation);
            return opener.Open(link, this, engine, apiKey);
        }
        catch
        {
            if (workspace is null && Directory.Exists(folder))
            {
                Directory.Delete(folder);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IPromptSession> Load(string id, PromptSessionOptions? options = null, CancellationToken cancellation = default)
    {
        if (options?.WorkspacePath is not null)
        {
            throw new ArgumentException("A loaded session keeps the workspace it was stored with; use AttachTo to change it.", nameof(options));
        }

        string? apiKey = options?.ApiKey;
        PromptSessionLink record = Guid.TryParse(id, out Guid guid) && await store.Find(guid, cancellation) is { } found ? found : throw new PromptSessionNotFoundException(id);
        Check(record, id);

        if ((record.AiSessionId ?? record.ForkedFrom) is { } origin && !engine.HasSession(origin))
        {
            throw new PromptSessionNotFoundException(id, $"The {engine.Identifier} session of session {id} no longer exists. If the tool was given another configuration folder than the one that made the session, use a provider with that engine; otherwise delete the session.");
        }

        if (record.WorkspacePath is null)
        {
            Directory.CreateDirectory(record.WorkingFolder);
        }

        return opener.Open(record, this, engine, apiKey);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default)
        => [.. (await store.List(cancellation))
            .Where(link => string.Equals(link.Provider, engine.Identifier, StringComparison.OrdinalIgnoreCase))
            .Select(link => new PromptSessionInfo { Id = link.Id.ToString("N"), Created = link.Created, WorkspacePath = link.WorkspacePath, IsStarted = link.AiSessionId is not null })];

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

            if (record.WorkspacePath is null && Path.GetFileName(record.WorkingFolder).StartsWith("markwardt.liveai-", StringComparison.Ordinal) && Directory.Exists(record.WorkingFolder))
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
    public async Task<PromptTestResult> Test(string? apiKey = null, CancellationToken cancellation = default)
    {
        try
        {
            await foreach (PromptEngineEvent output in runner.Run(engine, engine.CreateTestStart(apiKey), "Reply with the digit 1.", cancellation))
            {
                switch (output.Kind)
                {
                    case PromptEngineEventKind.Failed:
                        return Unavailable(output.Text);
                    case PromptEngineEventKind.Finished:
                        return new PromptTestResult { IsAvailable = true };
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
    public Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default)
        => engine.GetWindows(cancellation);

    private PromptTestResult Unavailable(string error)
        => new() { IsAvailable = false, Error = error };

    private void Check(PromptSessionLink record, string id)
    {
        if (!string.Equals(record.Provider, engine.Identifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The session {id} was made with '{record.Provider}', not with '{engine.Identifier}'.", nameof(id));
        }
    }
}
