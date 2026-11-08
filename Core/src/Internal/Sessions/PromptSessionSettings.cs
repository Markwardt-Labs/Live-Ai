namespace Markwardt.LiveAi;

/// <summary>
/// The settings of a session: values that the caller sets whenever it likes and that the session takes, all at once, when the next prompt starts. Every setter and every change of a collection checks its value at once, and tells the session through <see cref="Changed" /> so that it can end a process that is no longer wanted.
/// </summary>
internal sealed class PromptSessionSettings : IPromptSessionConfiguration, IPromptSessionTransientConfiguration
{
    private readonly object gate = new();

    private readonly Regex toolName = new("^[A-Za-z0-9_-]{1,64}$");

    private readonly IReadOnlyList<string> reservedToolNames;

    private readonly string reservedServerName = "liveai";

    private PromptCapability capability;

    private PromptEffort effort;

    private bool isShellAllowed;

    private bool isSystemAccessAllowed;

    private bool isWebAllowed;

    private bool isImageGenerationAllowed;

    private PromptStorageSettings storage;

    private PromptAsker? asker;

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
        capability = record.Capability;
        effort = record.Effort;
        isShellAllowed = record.IsShellAllowed;
        isSystemAccessAllowed = record.IsSystemAccessAllowed;
        isWebAllowed = record.IsWebAllowed;
        isImageGenerationAllowed = record.IsImageGenerationAllowed;
        instructions = record.Instructions;
        storage = record.Storage;
        Tools = new GuardedList<PromptTool>(gate, CheckTool, Begin, Notify);
        McpServers = new GuardedDictionary<PromptMcpServer>(gate, CheckServer, Begin, Notify, record.McpServers);
    }

    /// <summary>
    /// Occurs after a setting changed, on the thread that changed it and without a lock held.
    /// </summary>
    public event Action? Changed;

    /// <inheritdoc />
    public PromptCapability Capability
    {
        get
        {
            lock (gate)
            {
                return capability;
            }
        }

        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The capability is not a defined value.");
            }

            Set(() => capability = value);
        }
    }

    /// <inheritdoc />
    public PromptEffort Effort
    {
        get
        {
            lock (gate)
            {
                return effort;
            }
        }

        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The effort is not a defined value.");
            }

            Set(() => effort = value);
        }
    }

    /// <inheritdoc />
    public bool IsShellAllowed
    {
        get
        {
            lock (gate)
            {
                return isShellAllowed;
            }
        }

        set => Set(() => isShellAllowed = value);
    }

    /// <inheritdoc />
    public bool IsSystemAccessAllowed
    {
        get
        {
            lock (gate)
            {
                return isSystemAccessAllowed;
            }
        }

        set => Set(() => isSystemAccessAllowed = value);
    }

    /// <inheritdoc />
    public bool IsWebAllowed
    {
        get
        {
            lock (gate)
            {
                return isWebAllowed;
            }
        }

        set => Set(() => isWebAllowed = value);
    }

    /// <inheritdoc />
    public bool IsImageGenerationAllowed
    {
        get
        {
            lock (gate)
            {
                return isImageGenerationAllowed;
            }
        }

        set => Set(() => isImageGenerationAllowed = value);
    }

    /// <inheritdoc />
    public PromptStorageSettings Storage
    {
        get
        {
            lock (gate)
            {
                return storage;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Check();
            Set(() => storage = value);
        }
    }

    /// <inheritdoc />
    public PromptAsker? Asker
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
    /// Gets the external MCP servers by name.
    /// </summary>
    public GuardedDictionary<PromptMcpServer> McpServers { get; }

    IList<PromptTool> IPromptSessionTransientConfiguration.Tools => Tools;

    IDictionary<string, PromptMcpServer> IPromptSessionTransientConfiguration.McpServers => McpServers;

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
                Capability = capability,
                Effort = effort,
                IsShellAllowed = isShellAllowed,
                IsSystemAccessAllowed = isSystemAccessAllowed,
                IsWebAllowed = isWebAllowed,
                IsImageGenerationAllowed = isImageGenerationAllowed,
                Asker = asker,
                Instructions = instructions,
                Storage = storage,
                McpServers = McpServers.Copy(),
                Tools = Tools.Copy()
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

    private void CheckTool(PromptTool tool, IReadOnlyList<PromptTool> others)
    {
        if (!toolName.IsMatch(tool.Name) || reservedToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase) || others.Any(other => string.Equals(other.Name, tool.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Tool names must be 1 to 64 letters, digits, underscores and hyphens, unique, and not the name of a library tool.", nameof(tool));
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
