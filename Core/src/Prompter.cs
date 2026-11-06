namespace Markwardt.LiveAi;

/// <summary>
/// The entry point of the library: creates, loads, lists, deletes and tests the AI sessions of the tools it drives (Claude Code, Codex, and OpenCode for models served by Ollama). A session is stored in a folder, so it outlives the process that created it and can be loaded by another. Which tool a call is about, and how it is found and signed in, is what the <see cref="PromptSessionOptions" /> it is given say.
/// </summary>
public interface IPrompter
{
    /// <summary>
    /// Creates a session on the tool the options are for and stores it under a new identifier. The tool's own session starts with the first prompt, and the model is chosen then, from the capability and effort of the session's configuration.
    /// </summary>
    /// <param name="options">The tool, how it is used, and the workspace: a <see cref="ClaudeSessionOptions" />, <see cref="CodexSessionOptions" /> or <see cref="OllamaSessionOptions" />.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The new session, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The options are of a kind the library does not know, or name no model where they must (<see cref="OllamaSessionOptions.Models" />).</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace folder does not exist.</exception>
    Task<IPromptSession> Create(PromptSessionOptions options, CancellationToken cancellation = default);

    /// <summary>
    /// Loads a stored session, which continues the same conversation. The session records which tool runs it and how that tool is found and used, so its identifier is all it takes. Everything in its configuration comes back (the capability and effort, the switches, the instructions and the image generation); the API key is stored with the session, so a session made with one runs on it again. What the caller gave the session object itself (tools, external servers, the asker) is set again.
    /// </summary>
    /// <param name="id">The identifier of the session (<see cref="IPromptSession.Id" />).</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <returns>The session, which the caller disposes.</returns>
    /// <exception cref="PromptSessionNotFoundException">There is no such session, or the tool no longer has its conversation.</exception>
    /// <exception cref="PromptSessionException">The session was stored without the options it needs to be loaded.</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace folder the caller gave the session no longer exists.</exception>
    Task<IPromptSession> Load(string id, CancellationToken cancellation = default);

    /// <summary>
    /// Lists the stored sessions of every tool, newest first.
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
    /// Finds out whether a tool is usable, with the cheapest model it has and the smallest prompt that will do. A missing command, a missing sign in and a wrong key all show up as the reason, and not as a failure in the middle of real work. Where the tool says how much of the subscription is used, the result carries that too.
    /// </summary>
    /// <param name="options">The tool, and how it is used. The workspace is ignored.</param>
    /// <param name="cancellation">Cancels the test.</param>
    /// <returns>The result, which says why when the tool is not usable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
    Task<PromptTestResult> Test(PromptSessionOptions options, CancellationToken cancellation = default);
}

/// <inheritdoc />
public sealed class Prompter : IPrompter
{
    private readonly IPromptSessionStore store;

    private readonly IPromptSessionOpener opener;

    private readonly ITurnRunner runner;

    private readonly TimeProvider time;

    private readonly Func<PromptSessionOptions, IPromptEngine>? engines;

    private readonly Dictionary<string, PromptProvider> providers = [];

    private readonly object gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Prompter" /> class.
    /// </summary>
    /// <param name="dataFolder">The folder that holds the stored sessions, or <see langword="null" /> for <c>Markwardt.LiveAi</c> in the local application data folder. A session can only be loaded by a prompter that uses the same folder as the one that created it.</param>
    /// <param name="logger">Where the library reports what its sessions do (state changes, processes and tool servers started, input given to running work and failures, at debug level for the routine and at warning level for failures), or <see langword="null" /> to report nothing. Prompts, answers and files are never logged.</param>
    /// <param name="time">The clock the sessions run on (the pauses of the web search, the times of prompts), or <see langword="null" /> for the system's. Give a <see cref="TimeProvider" /> that you control to test code that waits.</param>
    public Prompter(string? dataFolder = null, ILogger? logger = null, TimeProvider? time = null)
        : this(new PromptSessionStore(dataFolder), new TurnRunner(), logger, time ?? TimeProvider.System)
    {
    }

    private Prompter(IPromptSessionStore store, ITurnRunner runner, ILogger? logger, TimeProvider time)
        : this(store, new PromptSessionOpener(store, runner, new PromptToolHost(), new PromptLibraryTools(null, time), logger, time), runner, time, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Prompter" /> class from the parts it works with.
    /// </summary>
    /// <param name="store">Keeps the stored sessions.</param>
    /// <param name="opener">Makes the session objects.</param>
    /// <param name="runner">Runs the engines' processes.</param>
    /// <param name="time">The clock that dates the sessions.</param>
    /// <param name="engines">Makes the engine that drives the tool of some options, or <see langword="null" /> for the real one.</param>
    internal Prompter(IPromptSessionStore store, IPromptSessionOpener opener, ITurnRunner runner, TimeProvider time, Func<PromptSessionOptions, IPromptEngine>? engines)
    {
        this.store = store;
        this.opener = opener;
        this.runner = runner;
        this.time = time;
        this.engines = engines;
    }

    /// <inheritdoc />
    public Task<IPromptSession> Create(PromptSessionOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Check(options);
        return Provider(options).Create(new PromptProviderOptions { ApiKey = ApiKey(options), Options = options, WorkspacePath = options.WorkspacePath }, cancellation);
    }

    /// <inheritdoc />
    public async Task<IPromptSession> Load(string id, CancellationToken cancellation = default)
    {
        PromptSessionOptions options = await Stored(id, cancellation);
        return await Provider(options).Load(id, new PromptProviderOptions { ApiKey = ApiKey(options) }, cancellation);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptSessionInfo>> List(CancellationToken cancellation = default)
        => [.. (await store.List(cancellation)).Select(link => new PromptSessionInfo { Id = link.Id.ToString("N"), Provider = link.Provider, Created = link.Created, WorkspacePath = link.WorkingFolder, IsStarted = link.AiSessionId is not null })];

    /// <inheritdoc />
    public async Task<bool> Delete(string id, CancellationToken cancellation = default)
    {
        if (!Guid.TryParse(id, out Guid guid) || await store.Find(guid, cancellation) is null)
        {
            return false;
        }

        return await Provider(await Stored(id, cancellation)).Delete(id, cancellation);
    }

    /// <inheritdoc />
    public Task<PromptTestResult> Test(PromptSessionOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Check(options);
        return Provider(options).Test(ApiKey(options), cancellation);
    }

    private async Task<PromptSessionOptions> Stored(string id, CancellationToken cancellation)
    {
        PromptSessionLink link = Guid.TryParse(id, out Guid guid) && await store.Find(guid, cancellation) is { } found ? found : throw new PromptSessionNotFoundException(id);

        try
        {
            return link.Options is { } options ? Checked(options) : throw Unreadable(id);
        }
        catch (ArgumentException)
        {
            throw Unreadable(id);
        }
    }

    private PromptSessionOptions Checked(PromptSessionOptions options)
    {
        Check(options);
        return options;
    }

    private void Check(PromptSessionOptions options)
    {
        if (options is OllamaSessionOptions ollama)
        {
            PromptCapabilityModels? models = ollama.Models;
            ArgumentNullException.ThrowIfNull(models, nameof(options));

            if (new[] { models.Fast, models.Balanced, models.Strong, models.Max }.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Every capability needs the name of a model to run.", nameof(options));
            }
        }
    }

    private PromptSessionException Unreadable(string id)
        => new($"The session {id} was stored without the options it needs to be loaded, so it cannot be loaded or deleted by its identifier.");

    private string? ApiKey(PromptSessionOptions options)
        => options switch
        {
            ClaudeSessionOptions claude => claude.ApiKey,
            CodexSessionOptions codex => codex.ApiKey,
            OllamaSessionOptions => null,
            _ => throw new ArgumentException($"{options.GetType().Name} is not a kind of session options the library knows.", nameof(options))
        };

    private IPromptEngine CreateEngine(PromptSessionOptions options)
        => options switch
        {
            ClaudeSessionOptions claude => new ClaudeEngine(claude.Command, claude.ConfigFolder),
            CodexSessionOptions codex => new CodexEngine(codex.Command, codex.ConfigFolder),
            OllamaSessionOptions ollama => new OpenCodeEngine(ollama.Command, [ollama.Server.ToString()], null, ollama.Models, ollama.Context),
            _ => throw new ArgumentException($"{options.GetType().Name} is not a kind of session options the library knows.", nameof(options))
        };

    private string EngineKey(PromptSessionOptions options)
        => options switch
        {
            ClaudeSessionOptions claude => $"claude|{claude.Command}|{claude.ConfigFolder}",
            CodexSessionOptions codex => $"codex|{codex.Command}|{codex.ConfigFolder}",
            OllamaSessionOptions ollama => $"ollama|{ollama.Command}|{ollama.Server}|{ollama.Models}|{ollama.Context}",
            _ => throw new ArgumentException($"{options.GetType().Name} is not a kind of session options the library knows.", nameof(options))
        };

    private PromptProvider Provider(PromptSessionOptions options)
    {
        string key = EngineKey(options);

        lock (gate)
        {
            if (!providers.TryGetValue(key, out PromptProvider? provider))
            {
                providers[key] = provider = new PromptProvider(engines?.Invoke(options) ?? CreateEngine(options), store, opener, runner, time);
            }

            return provider;
        }
    }
}
