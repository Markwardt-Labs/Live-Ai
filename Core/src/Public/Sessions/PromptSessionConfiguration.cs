namespace Markwardt.LiveAi;

/// <summary>
/// The settings of an <see cref="IPromptSession" />, which are changed by assigning to them or by changing their collections, at any time. Nothing happens at once: the session takes all the settings together when the next prompt starts, so changing several costs at most one restart of the AI's process, and a prompt that is running keeps what it started with. None of the settings is stored: a session that is loaded starts with the defaults, and the caller sets what it needs again. A value that is not allowed throws at the moment it is set and changes nothing, and once the session is disposed every change throws <see cref="ObjectDisposedException" />.
/// </summary>
public interface IPromptSessionConfiguration
{
    /// <summary>
    /// Gets or sets how capable the model is.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="PromptCapability" />.</exception>
    PromptCapability Capability { get; set; }

    /// <summary>
    /// Gets or sets how much reasoning effort the model puts into each prompt.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="PromptReasoning" />.</exception>
    PromptReasoning Reasoning { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI's process stays alive between prompts. When <see langword="true" /> the process starts with the first prompt and is never ended by the library, except when a changed setting needs a new one (the instructions, the external servers, the offered tools), a prompt is cancelled or the session is disposed; the model and the effort are changed in the running process. When <see langword="false" /> (the default) every prompt gets a process of its own, and a live process is ended in the background, after the prompt it is running. Only the providers the library drives through a conversation process (Claude Code and Codex) can stay alive; with a custom provider this has no effect and every prompt gets its own process.
    /// </summary>
    bool IsPersistent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI gets a shell tool (<c>shell</c>) with which it can run any command on this machine, as the user the host process runs as, with that user's full rights. It is <see langword="false" /> by default. This is outside everything else the library limits: mounts, read-only stores and the confinement of the file tools do not apply to what a command can reach, so turn it on only for an AI and a task that deserve that trust. The tool starts commands in the session's empty scratch folder unless the AI gives a working directory.
    /// </summary>
    bool EnableShell { get; set; }

    /// <summary>
    /// Gets or sets a function that is called before each call of a tool the library offers the AI (the shell, the file tools and the tools in <see cref="Tools" />) and decides whether it runs; it is given the same <see cref="PromptToolCallEvent" /> that <see cref="IPromptSession.Output" /> reports for the call (<see cref="PromptToolCallEvent.Id" /> is the AI tool's own identifier of the call where the tool says it, as Claude Code does, and the identifier of the request to the library's tool server otherwise). When it refuses the call (or throws) the call is not run and the AI is told it was refused, with the reason, so it can react. It is <see langword="null" /> by default, which lets every call run. It is read when each call arrives (also in a prompt that is running), it does not see the tools of external MCP servers, which the AI's tool calls itself, and a loaded session starts without one, like every setting.
    /// </summary>
    Func<PromptToolCallEvent, CancellationToken, Task<PromptToolDecision>>? BeforeToolCall { get; set; }

    /// <summary>
    /// Gets or sets a function that is called after each call of a tool the library offers the AI has run (whether it succeeded or failed), with the call, what became of it (a <see cref="PromptToolResultEvent" /> with the same <see cref="PromptToolCallEvent.Id" />) and how long it took, for logging or accounting. A call that was refused by <see cref="BeforeToolCall" /> did not run and is not reported. An exception it throws is ignored. It is <see langword="null" /> by default, it is read when each call ends, and a loaded session starts without one.
    /// </summary>
    Func<PromptToolCallEvent, PromptToolResultEvent, TimeSpan, CancellationToken, Task>? AfterToolCall { get; set; }

    /// <summary>
    /// Gets or sets what stops or repeats the requests of the session: its budget, timeout, window limit, retry and limit on tool calls. The default is no limit at all. Change one of them with <c>with</c> on the current record, for example <c>Limits = Limits with { Timeout = TimeSpan.FromMinutes(10) }</c>. Taken when the request starts.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A limit is not allowed: a budget that is not positive, a timeout or a window interval that is not positive or is longer than 24 days, a retry that is not valid, or a negative limit on tool calls.</exception>
    PromptSessionLimits Limits { get; set; }

    /// <summary>
    /// Gets or sets custom instructions for the AI, added to the library's own guidance for every prompt of the session, or <see langword="null" /> for none; blank instructions mean none. They can be changed at any time and the next prompt follows the current ones: Claude Code renders its system prompt again when it resumes, and for Codex, which keeps the instructions a conversation started with, the library adds the new ones to the conversation as a developer message with the next prompt.
    /// </summary>
    string? Instructions { get; set; }

    /// <summary>
    /// Gets the external MCP servers the AI can use besides the library's tools, each by name, which can be added to, removed from and cleared. Their tools are offered as they are and the AI may call them all (the tool is told to approve them), so add only servers that deserve that trust. A name is 1 to 64 letters, digits, underscores and hyphens, and is not <c>liveai</c>; a server has either a command or an http(s) address. Adding something that is not allowed throws at once and changes nothing.
    /// </summary>
    IDictionary<string, PromptMcpServer> McpServers { get; }

    /// <summary>
    /// Gets or sets the function that answers the questions of the AI, <see langword="null" /> by default. While it is set, the AI is offered a tool, <c>ask_user</c>, to put a question to the user and get the reply, and the tool calls this function with the question and returns the text it gives back as the user's reply; the AI waits for it, so the function can take as long as a person does, and the time counts toward the timeout of the request. Without a function the tool is not offered. It is read when a prompt starts to decide whether to offer the tool, and again when the tool is called; a question that arrives when it has been cleared is answered with an error that says no one is there to ask. An exception from the function is returned to the AI as the error of the tool. Like the other tools, the call goes through <see cref="BeforeToolCall" /> and <see cref="AfterToolCall" />, and it is stored with nothing, so a loaded session has none until it is set. The name <c>ask_user</c> is taken.
    /// </summary>
    Func<string, CancellationToken, Task<string>>? Asker { get; set; }

    /// <summary>
    /// Gets the functions of the caller's that the AI can call, which can be added to, removed from and cleared. A loaded session starts with none, like every setting, so they are set again. The names of the library's file tools (<c>list_directory</c>, <c>find_files</c>, <c>search_files</c>, <c>read_file</c>, <c>write_file</c>, <c>edit_file</c>, <c>delete_file</c>, <c>move_file</c>, <c>copy_file</c>) <c>shell</c> and <c>ask_user</c> are taken. The library serves them to the AI's tool on a local port, protected by a secret that only that tool gets. A name that is not 1 to 64 letters, digits, underscores and hyphens, is taken or is the name of another tool throws <see cref="ArgumentException" /> when it is added.
    /// </summary>
    IList<PromptTool> Tools { get; }

    /// <summary>
    /// Gets or sets the store that is the AI's current working directory for the file tools: a relative path the AI gives them is relative to it, unless it lies inside a mount. With a store of a folder on disk (<c>IStore.FromDisk</c>), whose absolute paths are those of this machine, the AI can also give absolute paths, and the shell starts in that folder. It is <see langword="null" /> by default, which gives the AI no working directory: it then reaches only what is mounted. A session created with a workspace path (see <see cref="IPromptProvider.Create" />) starts with the store of that folder, also when it is loaded; any other loaded session starts without one, like every setting. Only that path is stored: assigning this property changes the running session and nothing more, so it does not move the folder the tool runs in or what a later load starts with (the stored path, or none). Use <see cref="IPromptSession.AttachTo" /> to change the stored workspace.
    /// </summary>
    IStore? Workspace { get; set; }

    /// <summary>
    /// Gets the stores the AI can work on besides the <see cref="Workspace" />, each at a relative path, which can be added to, removed from and cleared. A path the AI gives the file tools that starts with the path of a mount (the longest that does) is inside that mount's store, with the path of the mount taken off: with a store mounted at <c>my/test</c>, the path <c>my/test/thing/file.txt</c> is the file <c>thing/file.txt</c> of that store, wherever the workspace has files. The AI can read, write, copy and move between them, and can reach nothing else (it has no shell unless <see cref="EnableShell" /> is on). A session with neither a workspace nor mounts has no access to files at all. Wrap a store with <see cref="StoreExtensions.AsReadOnly" /> to let the AI read but not change it, and use <c>IStore.FromDisk</c> for a folder on disk. A loaded session starts without mounts, like every setting, so they are mounted again. A path is made of names separated by <c>/</c>; one that is empty, has an empty name, a name that is <c>.</c> or <c>..</c>, or contains <c>\</c> throws <see cref="ArgumentException" /> when it is added.
    /// </summary>
    IDictionary<string, IStore> Mounts { get; }
}
