namespace Markwardt.LiveAi;

/// <summary>
/// What the library reports about the work of its sessions to the caller's logger, if there is one.
/// </summary>
internal interface IPromptLog
{
    /// <summary>
    /// Reports that a session changed state.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="state">The new state.</param>
    void State(string sessionId, PromptSessionState state);

    /// <summary>
    /// Reports what became of input given while the AI was working.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="taken">Whether the running process took the input into its work, and otherwise it waits for the next work.</param>
    void Steered(string sessionId, bool taken);

    /// <summary>
    /// Reports that a process of the tool was started for a session.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="engine">The tool.</param>
    void ProcessStarted(string sessionId, string engine);

    /// <summary>
    /// Reports that the server that offers the session's tools to the AI was started.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="tools">How many tools it offers.</param>
    void ToolServerStarted(string sessionId, int tools);

    /// <summary>
    /// Reports work of a session that failed.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="exception">The failure.</param>
    void WorkFailed(string sessionId, Exception exception);

    /// <summary>
    /// Reports that a session was branched, rewound or attached to another conversation.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="what">What was done.</param>
    void Moved(string sessionId, string what);

    /// <summary>
    /// Reports that what a loaded session was stored with was changed to fit the model as it is now.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="what">What was changed.</param>
    void ModelChanged(string sessionId, string what);

    /// <summary>
    /// Reports a problem with the storage of a session, such as files that could not be moved to disk.
    /// </summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <param name="what">What went wrong.</param>
    void StorageProblem(string sessionId, string what);
}

/// <inheritdoc />
internal sealed partial class PromptLog : IPromptLog
{
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptLog" /> class.
    /// </summary>
    /// <param name="logger">The caller's logger, or <see langword="null" /> to report nothing.</param>
    public PromptLog(ILogger? logger)
        => this.logger = logger ?? NullLogger.Instance;

    /// <inheritdoc />
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Session {SessionId} is now {State}.")]
    public partial void State(string sessionId, PromptSessionState state);

    /// <inheritdoc />
    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Session {SessionId} gave input to the running work (taken: {Taken}).")]
    public partial void Steered(string sessionId, bool taken);

    /// <inheritdoc />
    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Session {SessionId} started a {Engine} process.")]
    public partial void ProcessStarted(string sessionId, string engine);

    /// <inheritdoc />
    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Session {SessionId} started the tool server with {Tools} tools.")]
    public partial void ToolServerStarted(string sessionId, int tools);

    /// <inheritdoc />
    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Work of session {SessionId} failed.")]
    public partial void WorkFailed(string sessionId, Exception exception);

    /// <inheritdoc />
    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Session {SessionId}: {What}.")]
    public partial void Moved(string sessionId, string what);

    /// <inheritdoc />
    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Session {SessionId}: {What}.")]
    public partial void ModelChanged(string sessionId, string what);

    /// <inheritdoc />
    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Session {SessionId}: {What}")]
    public partial void StorageProblem(string sessionId, string what);
}
