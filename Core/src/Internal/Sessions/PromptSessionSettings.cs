namespace Markwardt.LiveAi;

/// <summary>
/// The settings of a session: values that the caller sets whenever it likes and that the session takes, all at once, when the next prompt starts. Every setter and every change of a collection checks its value at once, and tells the session through <see cref="Changed" /> so that it can end a process that is no longer wanted.
/// </summary>
internal sealed class PromptSessionSettings : IPromptSessionConfiguration
{
    private readonly object gate = new();

    private readonly Regex toolName = new("^[A-Za-z0-9_-]{1,64}$");

    private readonly Regex mountName = new("^(?!\\.\\.?(?:/|$))[^/\\\\]+(?:/(?!\\.\\.?(?:/|$))[^/\\\\]+)*$");

    private readonly IReadOnlyList<string> reservedToolNames;

    private readonly string reservedServerName = "liveai";

    private PromptModel? model;

    private bool isPersistent;

    private bool enableShell;

    private IStore? workspace;

    private Func<PromptToolCallEvent, CancellationToken, Task<PromptToolDecision>>? beforeToolCall;

    private Func<PromptToolCallEvent, PromptToolResultEvent, TimeSpan, CancellationToken, Task>? afterToolCall;

    private Func<string, CancellationToken, Task<string>>? asker;

    private PromptSessionLimits limits = new();

    private string? instructions;

    private bool closed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptSessionSettings" /> class with the settings a session starts with.
    /// </summary>
    /// <param name="record">The session as it is opened: its settings start from the values in it.</param>
    /// <param name="reservedToolNames">The names a caller's tool cannot use.</param>
    public PromptSessionSettings(PromptSessionRecord record, IReadOnlyList<string> reservedToolNames)
    {
        this.reservedToolNames = reservedToolNames;
        model = record.Model;
        instructions = record.Instructions;
        Tools = new GuardedList<PromptTool>(gate, CheckTool, Begin, Notify);
        Mounts = new GuardedDictionary<IStore>(gate, CheckMount, Begin, Notify, []);
        McpServers = new GuardedDictionary<PromptMcpServer>(gate, CheckServer, Begin, Notify, record.McpServers);
        ModelSettings = new GuardedDictionary<string>(gate, CheckModelSetting, Begin, Notify, record.ModelSettings);
    }

    /// <summary>
    /// Occurs after a setting changed, on the thread that changed it and without a lock held.
    /// </summary>
    public event Action? Changed;

    /// <inheritdoc />
    public PromptModel? Model
    {
        get
        {
            lock (gate)
            {
                return model;
            }
        }

        set => Set(() =>
        {
            model = value;
            ModelSettings.Replace(value is null ? [] : value.Reconcile(ModelSettings.Copy()).Settings);
        });
    }

    /// <inheritdoc />
    public bool IsPersistent
    {
        get
        {
            lock (gate)
            {
                return isPersistent;
            }
        }

        set => Set(() => isPersistent = value);
    }

    /// <inheritdoc />
    public bool EnableShell
    {
        get
        {
            lock (gate)
            {
                return enableShell;
            }
        }

        set => Set(() => enableShell = value);
    }

    /// <inheritdoc />
    public IStore? Workspace
    {
        get
        {
            lock (gate)
            {
                return workspace;
            }
        }

        set => Set(() => workspace = value);
    }

    /// <inheritdoc />
    public Func<PromptToolCallEvent, CancellationToken, Task<PromptToolDecision>>? BeforeToolCall
    {
        get
        {
            lock (gate)
            {
                return beforeToolCall;
            }
        }

        set => Set(() => beforeToolCall = value);
    }

    /// <inheritdoc />
    public Func<PromptToolCallEvent, PromptToolResultEvent, TimeSpan, CancellationToken, Task>? AfterToolCall
    {
        get
        {
            lock (gate)
            {
                return afterToolCall;
            }
        }

        set => Set(() => afterToolCall = value);
    }

    /// <inheritdoc />
    public Func<string, CancellationToken, Task<string>>? Asker
    {
        get
        {
            lock (gate)
            {
                return asker;
            }
        }

        set => Set(() => asker = value);
    }

    public PromptSessionLimits Limits
    {
        get
        {
            lock (gate)
            {
                return limits;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Problem is { } problem)
            {
                throw new ArgumentException(problem, nameof(value));
            }

            Set(() => limits = value);
        }
    }

    /// <inheritdoc />
    public string? Instructions
    {
        get
        {
            lock (gate)
            {
                return instructions;
            }
        }

        set => Set(() => instructions = string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>
    /// Gets the caller's tools, with the operations the session itself needs.
    /// </summary>
    public GuardedList<PromptTool> Tools { get; }

    /// <summary>
    /// Gets the mounted stores by name.
    /// </summary>
    public GuardedDictionary<IStore> Mounts { get; }

    /// <summary>
    /// Gets the external MCP servers by name.
    /// </summary>
    public GuardedDictionary<PromptMcpServer> McpServers { get; }

    /// <summary>
    /// Gets the values of the selected model's settings.
    /// </summary>
    public GuardedDictionary<string> ModelSettings { get; }

    /// <inheritdoc />
    IList<PromptTool> IPromptSessionConfiguration.Tools => Tools;

    /// <inheritdoc />
    IDictionary<string, IStore> IPromptSessionConfiguration.Mounts => Mounts;

    /// <inheritdoc />
    IDictionary<string, PromptMcpServer> IPromptSessionConfiguration.McpServers => McpServers;

    IDictionary<string, string> IPromptSessionConfiguration.ModelSettings => ModelSettings;

    /// <summary>
    /// Gets the limits a request runs under, as the settings say now, with the window limit of a loop added.
    /// </summary>
    /// <param name="extra">The limits of a loop, whose window limit is added, or <see langword="null" /> for none.</param>
    /// <returns>The limits.</returns>
    public PromptRequestLimits RequestLimits(PromptRequestLimits? extra = null)
    {
        PromptSessionSettingsSnapshot snapshot = Snapshot();

        return new PromptRequestLimits
        {
            Timeout = snapshot.Limits.Timeout,
            WindowLimit = snapshot.Limits.WindowLimit,
            Retry = snapshot.Limits.Retry,
            MaxToolCalls = snapshot.Limits.MaxToolCalls
        }.WithWindowOf(extra);
    }

    /// <summary>
    /// Takes the settings as they are now.
    /// </summary>
    /// <returns>A copy that does not change afterwards.</returns>
    public PromptSessionSettingsSnapshot Snapshot()
    {
        lock (gate)
        {
            return new PromptSessionSettingsSnapshot
            {
                Model = model,
                ModelSettings = ModelSettings.Copy(),
                IsPersistent = isPersistent,
                EnableShell = enableShell,
                Workspace = workspace,
                BeforeToolCall = beforeToolCall,
                AfterToolCall = afterToolCall,
                Asker = asker,
                Limits = limits,
                Instructions = instructions,
                McpServers = McpServers.Copy(),
                Tools = Tools.Copy(),
                Mounts = Mounts.Copy()
            };
        }
    }

    /// <summary>
    /// Stops accepting changes, which then throw <see cref="ObjectDisposedException" />.
    /// </summary>
    public void Close()
    {
        lock (gate)
        {
            closed = true;
        }
    }

    private void Set(Action assign)
    {
        lock (gate)
        {
            Begin();
            assign();
        }

        Notify();
    }

    private void Begin()
    {
        if (closed)
        {
            throw new ObjectDisposedException("The session was disposed.");
        }
    }

    private void Notify()
        => Changed?.Invoke();

    private void CheckModelSetting(string id, string value)
    {
        if (model is null)
        {
            throw new PromptSessionModelException("Select a model before changing its settings.");
        }

        if (model.Settings is null)
        {
            return;
        }

        PromptModelSetting setting = model.Settings.FirstOrDefault(offered => offered.Id == id) ?? throw new ArgumentException($"The model {model.Id} has no setting '{id}'.", nameof(id));

        if (!setting.Accepts(value))
        {
            throw new ArgumentException($"The setting '{id}' does not accept '{value}'.", nameof(value));
        }
    }

    private void CheckTool(PromptTool tool, IReadOnlyList<PromptTool> others)
    {
        if (!toolName.IsMatch(tool.Name) || reservedToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase) || others.Any(other => string.Equals(other.Name, tool.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Tool names must be 1 to 64 letters, digits, underscores and hyphens, unique, and not the name of a library tool.", nameof(tool));
        }
    }

    private void CheckMount(string name, IStore store)
    {
        if (!mountName.IsMatch(name))
        {
            throw new ArgumentException("A mount path must be names separated by /, none of them empty, . or .., and none containing \\.", nameof(name));
        }
    }

    private void CheckServer(string name, PromptMcpServer server)
    {
        if (!toolName.IsMatch(name) || name == reservedServerName || server is null || (server.Command is null) == (server.Url is null) || (server.Command is null ? !(Uri.TryCreate(server.Url, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https") : string.IsNullOrWhiteSpace(server.Command)))
        {
            throw new ArgumentException("An MCP server needs a name of 1 to 64 letters, digits, underscores and hyphens that is not liveai, and either a command or an http(s) address.", nameof(name));
        }
    }
}
