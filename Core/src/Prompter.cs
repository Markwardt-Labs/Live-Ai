namespace Markwardt.LiveAi;

/// <summary>
/// The entry point of the library: creates, loads, lists, deletes and tests the AI sessions of the tools it drives (Claude Code, Codex, and OpenCode for models on this machine or the network). A session is stored in a folder, so it outlives the process that created it and can be loaded by another. Which tool a call is about, and the key it signs in with, is what the <see cref="PromptSetup" /> it is given says; where the tools are is the <see cref="IPrompter.Environment" />.
/// </summary>
public interface IPrompter
{
    /// <summary>
    /// Gets or sets how things run in the environment: where each tool is, where it keeps its configuration, the default API keys, the server and models of the local models, the image generation service, and the configuration that new sessions start with. It applies to every session of this prompter, is not stored with the sessions, and is read at each call, so a change reaches the sessions created, loaded, deleted and tested after it (a session that is open keeps the tool it was started with). It starts as a default <see cref="PromptEnvironment" />, which finds each tool on the <c>PATH</c> and Ollama on its usual address.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null" />.</exception>
    PromptEnvironment Environment { get; set; }

    /// <summary>
    /// Creates a session on the tool the setup is for and stores it under a new identifier. The tool's own session starts with the first prompt, and the model is chosen then, from the capability and effort of the session's configuration.
    /// </summary>
    /// <param name="setup">The tool, its sign in, and the workspace: a <see cref="ClaudeSetup" />, <see cref="CodexSetup" /> or <see cref="LocalSetup" />. A Claude Code or Codex setup without an API key gets the one of the environment (<see cref="PromptEnvironment.ClaudeApiKey" />, <see cref="PromptEnvironment.CodexApiKey" />), if it has one.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The new session, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="setup" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The setup is of a kind the library does not know, the local models have none named (the environment, <see cref="PromptEnvironment.LocalSettings" />, or the setup, <see cref="LocalSetup.Overrides" />, must name them), the capability or the effort of the environment is not a defined value, or the environment allows image generation (<see cref="PromptEnvironment.IsImageGenerationAllowed" />) and its image generation (<see cref="PromptEnvironment.ImageGeneration" />) is not valid (an empty API key, say).</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace folder does not exist.</exception>
    Task<IPromptSession> Create(PromptSetup setup, CancellationToken cancellation = default);

    /// <summary>
    /// Loads a stored session, which continues the same conversation. The session records which tool runs it and the setup it was made with, so its identifier is all it takes; the tool is found as the current <see cref="Environment" /> says, and the key is the one it was created with. Everything in its configuration comes back (the capability and effort, the switches, among them whether image generation is allowed, and the instructions), while the image generation service itself, with its key, is the one of the current environment; the API key is stored with the session, so a session made with one runs on it again. What the caller gave the session object itself (tools, external servers, the asker) is set again.
    /// </summary>
    /// <param name="id">The identifier of the session (<see cref="IPromptSession.Id" />).</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <returns>The session, which the caller disposes.</returns>
    /// <exception cref="PromptSessionNotFoundException">There is no such session, or the tool no longer has its conversation.</exception>
    /// <exception cref="PromptSessionException">The session was stored without the setup it needs to be loaded.</exception>
    /// <exception cref="ArgumentException">The session runs local models, and neither the environment (<see cref="PromptEnvironment.LocalSettings" />) nor its stored setup names them; or the session allows image generation and the image generation of the environment (<see cref="PromptEnvironment.ImageGeneration" />) is not valid.</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace folder the caller gave the session no longer exists.</exception>
    Task<IPromptSession> Load(string id, CancellationToken cancellation = default);

    /// <summary>
    /// Lists the stored sessions of every tool, the most recently loaded first (<see cref="PromptSessionInfo.LastLoaded" />).
    /// </summary>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>What is known about each session.</returns>
    Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default);

    /// <summary>
    /// Deletes a stored session for good: the link to the tool's conversation, the conversation itself, and the workspace folder the library made for it, with whatever the AI wrote there, so nothing is left to clean up. The tool it ran on is read from the session. A session that is open should be disposed first. A workspace folder is not touched.
    /// </summary>
    /// <param name="id">The identifier of the session.</param>
    /// <param name="cancellation">Cancels the deletion.</param>
    /// <returns><see langword="true" /> when the session was there and is gone, <see langword="false" /> when there was no such session.</returns>
    /// <exception cref="PromptSessionException">The conversation or the folder could not be removed.</exception>
    Task<bool> Delete(string id, CancellationToken cancellation = default);

    /// <summary>
    /// Moves a stored session to another workspace folder, which is what the library tracks as the place of a workspace, and keeps its conversation, without loading it: the folder it was in may be gone already, for instance because it was moved on disk, which is what loading a session cannot cope with. It does what <see cref="IPromptSession.Move" /> does for a session that is open: the tool's conversation is brought to the new folder where the tool keeps it by folder (Claude Code copies it and leaves the old copy), the folder the library made for a session that had none is removed, and the link is stored with the new folder. The system access switch and every other setting are kept. A session that is open should be disposed first. Nothing on disk is moved: the files of the workspace are the caller's.
    /// </summary>
    /// <param name="id">The identifier of the session.</param>
    /// <param name="workspacePath">The existing folder that is the session's workspace from now on.</param>
    /// <param name="cancellation">Cancels the move.</param>
    /// <exception cref="ArgumentNullException"><paramref name="workspacePath" /> is <see langword="null" />.</exception>
    /// <exception cref="PromptSessionNotFoundException">There is no such session.</exception>
    /// <exception cref="PromptSessionException">The session was stored without the setup it needs, or the tool's conversation could not be brought to the folder. Nothing has changed.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist. Nothing has changed.</exception>
    Task Move(string id, string workspacePath, CancellationToken cancellation = default);

    /// <summary>
    /// Finds out whether a tool is usable, with the cheapest model it has and the smallest prompt that will do. A missing command, a missing sign in and a wrong key all show up as the reason, and not as a failure in the middle of real work. Where the tool says how much of the subscription is used, the result carries that too.
    /// </summary>
    /// <param name="setup">The tool, and the key it signs in with (the one of the environment when the setup has none). The workspace is ignored.</param>
    /// <param name="cancellation">Cancels the test.</param>
    /// <returns>The result, which says why when the tool is not usable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="setup" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The setup is of a kind the library does not know, or local models have none named by the environment or the setup.</exception>
    Task<PromptTestResult> Test(PromptSetup setup, CancellationToken cancellation = default);
}

/// <inheritdoc />
public sealed class Prompter : IPrompter
{
    private readonly IPromptSessionStore store;

    private readonly IPromptSessionOpener opener;

    private readonly ITurnRunner runner;

    private readonly TimeProvider time;

    private readonly Func<PromptSetup, IPromptEngine>? engines;

    private readonly Dictionary<string, PromptToolService> services = [];

    private readonly object gate = new();

    private readonly StrongBox<PromptEnvironment> current;

    /// <summary>
    /// Initializes a new instance of the <see cref="Prompter" /> class.
    /// </summary>
    /// <param name="dataFolder">The folder that holds the stored sessions, or <see langword="null" /> for <c>Markwardt.LiveAi</c> in the local application data folder. A session can only be loaded by a prompter that uses the same folder as the one that created it.</param>
    /// <param name="logger">Where the library reports what its sessions do (state changes, processes and tool servers started, input given to running work and failures, at debug level for the routine and at warning level for failures), or <see langword="null" /> to report nothing. Prompts, answers and files are never logged.</param>
    /// <param name="time">The clock the sessions run on (the pauses of the web search, the times of prompts), or <see langword="null" /> for the system's. Give a <see cref="TimeProvider" /> that you control to test code that waits.</param>
    public Prompter(string? dataFolder = null, ILogger? logger = null, TimeProvider? time = null)
        : this(new PromptSessionStore(dataFolder), new TurnRunner(), logger, time ?? TimeProvider.System, new StrongBox<PromptEnvironment>(new PromptEnvironment()))
    {
    }

    private Prompter(IPromptSessionStore store, ITurnRunner runner, ILogger? logger, TimeProvider time, StrongBox<PromptEnvironment> current)
        : this(store, new PromptSessionOpener(store, runner, new PromptToolHost(), new PromptLibraryTools(null, time), logger, time, () => current.Value!.ImageGeneration), runner, time, null, current)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Prompter" /> class from the parts it works with.
    /// </summary>
    /// <param name="store">Keeps the stored sessions.</param>
    /// <param name="opener">Makes the session objects.</param>
    /// <param name="runner">Runs the engines' processes.</param>
    /// <param name="time">The clock that dates the sessions.</param>
    /// <param name="engines">Makes the engine that drives the tool of some setup, or <see langword="null" /> for the real one.</param>
    internal Prompter(IPromptSessionStore store, IPromptSessionOpener opener, ITurnRunner runner, TimeProvider time, Func<PromptSetup, IPromptEngine>? engines)
        : this(store, opener, runner, time, engines, new StrongBox<PromptEnvironment>(new PromptEnvironment()))
    {
    }

    private Prompter(IPromptSessionStore store, IPromptSessionOpener opener, ITurnRunner runner, TimeProvider time, Func<PromptSetup, IPromptEngine>? engines, StrongBox<PromptEnvironment> current)
    {
        this.current = current;
        this.store = store;
        this.opener = opener;
        this.runner = runner;
        this.time = time;
        this.engines = engines;
    }

    /// <inheritdoc />
    public PromptEnvironment Environment
    {
        get => current.Value!;
        set => current.Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <inheritdoc />
    public Task<IPromptSession> Create(PromptSetup setup, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        PromptEnvironment environment = Environment;
        Check(setup, environment);
        CheckConfiguration(environment);
        CheckImages(environment.IsImageGenerationAllowed, environment);
        PromptSetup given = WithKey(setup, environment);
        return Service(given, environment).Create(new PromptToolServiceOptions { ApiKey = ApiKey(given), Setup = given, Environment = environment, WorkspacePath = given.WorkspacePath }, cancellation);
    }

    /// <inheritdoc />
    public async Task<IPromptSession> Load(string id, CancellationToken cancellation = default)
    {
        PromptSessionLink link = await Linked(id, cancellation);
        PromptSetup setup = link.Setup ?? throw Unreadable(id);
        PromptEnvironment environment = Environment;
        Check(setup, environment);
        CheckImages(link.IsImageGenerationAllowed, environment);
        return await Service(setup, environment).Load(id, new PromptToolServiceOptions { ApiKey = ApiKey(setup) }, cancellation);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default)
        => [.. (await store.List(cancellation)).Select(link => new PromptSessionInfo { Id = link.Id.ToString("N"), Provider = link.Provider, LastLoaded = link.LastLoaded, WorkspacePath = link.WorkingFolder })];

    /// <inheritdoc />
    public async Task<bool> Delete(string id, CancellationToken cancellation = default)
    {
        if (!Guid.TryParse(id, out Guid guid) || await store.Find(guid, cancellation) is null)
        {
            return false;
        }

        return await Service(await Stored(id, cancellation), Environment).Delete(id, cancellation);
    }

    /// <inheritdoc />
    public async Task Move(string id, string workspacePath, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        await Service(await Stored(id, cancellation), Environment).Move(id, workspacePath, cancellation);
    }

    /// <inheritdoc />
    public Task<PromptTestResult> Test(PromptSetup setup, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        PromptEnvironment environment = Environment;
        Check(setup, environment);
        PromptSetup given = WithKey(setup, environment);
        return Service(given, environment).Test(ApiKey(given), cancellation);
    }

    private async Task<PromptSessionLink> Linked(string id, CancellationToken cancellation)
        => Guid.TryParse(id, out Guid guid) && await store.Find(guid, cancellation) is { } found ? found : throw new PromptSessionNotFoundException(id);

    private async Task<PromptSetup> Stored(string id, CancellationToken cancellation)
        => (await Linked(id, cancellation)).Setup ?? throw Unreadable(id);

    private (Uri Server, PromptCapabilityModels? Models, PromptCapabilityContexts? Contexts) Settle(LocalSetup setup, PromptEnvironment environment)
        => (setup.Overrides.Server ?? environment.LocalSettings.Server ?? PromptServerSettings.DefaultServer, setup.Overrides.Models ?? environment.LocalSettings.Models, setup.Overrides.Contexts ?? environment.LocalSettings.Contexts);

    private void CheckConfiguration(PromptEnvironment environment)
    {
        if (!Enum.IsDefined(environment.Capability))
        {
            throw new ArgumentException("The capability of the environment is not a defined value.", nameof(environment));
        }

        if (!Enum.IsDefined(environment.Effort))
        {
            throw new ArgumentException("The effort of the environment is not a defined value.", nameof(environment));
        }
    }

    private void Check(PromptSetup setup, PromptEnvironment environment)
    {
        if (setup is LocalSetup local)
        {
            PromptCapabilityModels models = Settle(local, environment).Models ?? throw new ArgumentException("Local models need the model to run for each capability, and neither the environment nor the setup of the session name them.", nameof(setup));

            if (new[] { models.Fast, models.Balanced, models.Strong, models.Max }.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Every capability needs the name of a model to run.", nameof(setup));
            }
        }
    }

    private void CheckImages(bool allowed, PromptEnvironment environment)
    {
        if (allowed)
        {
            environment.ImageGeneration?.Validate();
        }
    }

    private PromptSessionException Unreadable(string id)
        => new($"The session {id} was stored without the setup it needs to be loaded, so it cannot be loaded or deleted by its identifier.");

    private PromptSetup WithKey(PromptSetup setup, PromptEnvironment environment)
        => setup switch
        {
            ClaudeSetup { ApiKey: null } claude => claude with { ApiKey = environment.ClaudeApiKey },
            CodexSetup { ApiKey: null } codex => codex with { ApiKey = environment.CodexApiKey },
            _ => setup
        };

    private string? ApiKey(PromptSetup setup)
        => setup switch
        {
            ClaudeSetup claude => claude.ApiKey,
            CodexSetup codex => codex.ApiKey,
            LocalSetup => null,
            _ => throw new ArgumentException($"{setup.GetType().Name} is not a kind of session setup the library knows.", nameof(setup))
        };

    private IPromptEngine CreateEngine(PromptSetup setup, PromptEnvironment environment)
        => setup switch
        {
            ClaudeSetup => new ClaudeEngine(environment.ClaudeCommand, environment.ClaudeConfigFolder),
            CodexSetup => new CodexEngine(environment.CodexCommand, environment.CodexConfigFolder),
            LocalSetup local => CreateOpenCodeEngine(local, environment),
            _ => throw new ArgumentException($"{setup.GetType().Name} is not a kind of session setup the library knows.", nameof(setup))
        };

    private OpenCodeEngine CreateOpenCodeEngine(LocalSetup setup, PromptEnvironment environment)
    {
        (Uri server, PromptCapabilityModels? models, PromptCapabilityContexts? contexts) = Settle(setup, environment);
        return new OpenCodeEngine(environment.LocalCommand, [server.ToString()], null, models, contexts);
    }

    private string EngineKey(PromptSetup setup, PromptEnvironment environment)
        => setup switch
        {
            ClaudeSetup => $"claude|{environment.ClaudeCommand}|{environment.ClaudeConfigFolder}",
            CodexSetup => $"codex|{environment.CodexCommand}|{environment.CodexConfigFolder}",
            LocalSetup local => $"local|{environment.LocalCommand}|{Settle(local, environment)}",
            _ => throw new ArgumentException($"{setup.GetType().Name} is not a kind of session setup the library knows.", nameof(setup))
        };

    private PromptToolService Service(PromptSetup setup, PromptEnvironment environment)
    {
        string key = EngineKey(setup, environment);

        lock (gate)
        {
            if (!services.TryGetValue(key, out PromptToolService? service))
            {
                services[key] = service = new PromptToolService(engines?.Invoke(setup) ?? CreateEngine(setup, environment), store, opener, runner, time);
            }

            return service;
        }
    }
}
