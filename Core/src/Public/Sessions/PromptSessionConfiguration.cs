namespace Markwardt.LiveAi;

/// <summary>
/// The settings of an <see cref="IPromptSession" />, which are changed by assigning to them or by changing their collections, at any time. Nothing happens at once: the session takes all the settings together when the next prompt starts, so changing several costs at most one restart of the AI's process, and a prompt that is running keeps what it started with. Only the model and its settings are stored with the session: a session that is loaded starts with them (brought up to date with what the model offers now), and the caller sets everything else again. A value that is not allowed throws at the moment it is set and changes nothing, and once the session is disposed every change throws <see cref="ObjectDisposedException" />.
/// </summary>
public interface IPromptSessionConfiguration
{
    /// <summary>
    /// Gets or sets the model the session runs, one of the models <see cref="IPromptProvider.GetModels" /> lists, or <see langword="null" /> while none is selected. A session needs a model to do anything with the tool: <c>Request</c>, <c>Send</c>, <c>Loop</c>, <c>Compact</c> and <c>GetContext</c> throw <see cref="PromptSessionModelException" /> until one is. Changing the model keeps the values of the settings both models have and gives the rest their defaults, so <see cref="ModelSettings" /> always holds a value for every setting the model offers.
    /// </summary>
    PromptModel? Model { get; set; }

    /// <summary>
    /// Gets the values of the selected model's settings, by <see cref="PromptModelSetting.Id" />, as text. A value is checked when it is set: it has to be one the setting accepts.
    /// </summary>
    /// <exception cref="ArgumentException">Assigning a value: the model has no such setting, or the setting does not accept the value.</exception>
    /// <exception cref="PromptSessionModelException">Assigning a value: no model is selected.</exception>
    IDictionary<string, string> ModelSettings { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI's process stays alive between prompts. When <see langword="true" /> the process starts with the first prompt and is never ended by the library, except when a changed setting needs a new one (the instructions, the external servers, the offered tools), a prompt is cancelled or the session is disposed; the model and its settings are changed in the running process (except for a tool that takes them when it starts, which gets a new one). When <see langword="false" /> (the default) every prompt gets a process of its own, and a live process is ended in the background, after the prompt it is running. Only the providers the library drives through a conversation process (Claude Code and Codex) can stay alive; with a custom provider this has no effect and every prompt gets its own process.
    /// </summary>
    bool IsPersistent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI gets a shell tool (<c>shell</c>) with which it can run any command on this machine, as the user the host process runs as, with that user's full rights. It is <see langword="false" /> by default. This is outside everything else the library limits: mounts, read-only stores and the confinement of the file tools do not apply to what a command can reach, so turn it on only for an AI and a task that deserve that trust. The tool starts commands in the session's empty scratch folder unless the AI gives a working directory.
    /// </summary>
    bool EnableShell { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI gets a tool (<c>view_image</c>) with which it can look at an image file (PNG, JPEG, GIF or WebP, up to 5 MB) of the workspace or a mount. A tool can only answer in text, so the image is given to the AI as an input of its own, with the image attached, which is steered into the turn the AI is working on (and sent as an input after it when none is running); the tool's answer says so, and tells the AI to call it again should the input not arrive. It is <see langword="false" /> by default, and the tool is offered only while the session has files to read, the selected model takes images and the engine can be shown images.
    /// </summary>
    bool EnableImages { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI gets a tool (<c>fetch_url</c>) with which it can read a web page or other text from an http or https address on the internet, as this machine and with its network access (addresses on the local network and on this machine included). It is <see langword="false" /> by default.
    /// </summary>
    bool EnableWeb { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI can search the web. When the tool has a web search of its own (Claude Code and Codex do) the AI is allowed to use it, and the library does not see or filter what it searches or reads; otherwise the AI is given a tool, <c>search_web</c>, that asks DuckDuckGo's plain HTML page, which needs no key and no other software but is best effort: the page is read, not an API, so it can change or turn the library away, and the library spaces its requests at least two seconds apart and stops for a minute when it is turned away. <see cref="WebFilter" /> applies to neither. It is <see langword="false" /> by default and not stored with a loaded session, and changing it ends a kept process of a tool that takes it when it starts.
    /// </summary>
    bool EnableSearch { get; set; }

    /// <summary>
    /// Gets or sets the generator that makes the images the AI asks for, <see langword="null" /> by default, in which case the AI has no tool to make images. While one is set, and the session has files to save them in (a workspace or a mount), the AI is offered <c>generate_image</c>, which saves each image as a file where the AI says and never replaces a file; the AI is only offered what the generator supports (<see cref="IPromptImageGenerator.Features" />). Make one with <c>IPromptImageGenerator.OpenAi</c> or <c>IPromptImageGenerator.StableDiffusion</c>. Images can cost money or take a graphics card for minutes, so the call goes through <see cref="BeforeToolCall" /> like the other tools, where it can be approved or refused. It is read when a prompt starts, is stored with nothing, so a loaded session has none until it is set, and the name <c>generate_image</c> is taken.
    /// </summary>
    IPromptImageGenerator? ImageGenerator { get; set; }

    /// <summary>
    /// Gets or sets the function that decides which addresses <c>fetch_url</c> (see <see cref="EnableWeb" />) may read, <see langword="null" /> by default, which allows every address. It is called with the address of every request the tool makes, the addresses a page redirects to included, and returns <see langword="true" /> to block the request: the AI is told that the address is blocked, and nothing is sent to it. An exception from the function (other than cancellation) blocks the request too. It is read when each request is made, also in a prompt that is running, so it can be changed at any time, it does not restart anything, and it is stored with nothing, so a loaded session has none until it is set.
    /// </summary>
    Func<Uri, CancellationToken, Task<bool>>? WebFilter { get; set; }

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
