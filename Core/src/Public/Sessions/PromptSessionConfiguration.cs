namespace Markwardt.LiveAi;

/// <summary>
/// The settings of an <see cref="IPromptSession" />, which are changed by assigning to them or by changing their collections, at any time. Nothing happens at once: the session takes all the settings together when the next prompt starts, so changing several costs at most one restart of the AI's process, and a prompt that is running keeps what it started with. All of it is stored with the session, so a session that is loaded starts with it; the tools, MCP servers and asker are in <see cref="IPromptSessionTransientConfiguration" />. A value that is not allowed throws at the moment it is set and changes nothing, and once the session is disposed every change throws <see cref="ObjectDisposedException" />.
/// </summary>
public interface IPromptSessionConfiguration
{
    /// <summary>
    /// Gets or sets how capable the model is, <see cref="PromptCapability.Balanced" /> by default. The library picks the model of the tool that fits, in the background, when the next prompt starts, so a caller never lists or names models; changing it between prompts changes the model from then on, and the conversation goes on. It is stored with the session.
    /// </summary>
    PromptCapability Capability { get; set; }

    /// <summary>
    /// Gets or sets how much effort the model puts into each prompt, <see cref="PromptEffort.Medium" /> by default. The library maps it to the nearest level the chosen model offers, and a model with no such setting ignores it. It is stored with the session.
    /// </summary>
    PromptEffort Effort { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI gets a shell tool (<c>shell</c>) with which it can run any command on this machine, as the user the host process runs as, with that user's full rights. It is <see langword="false" /> by default. This is outside everything else the library limits: the sandbox and the confinement of the file tools do not apply to what a command can reach, so turn it on only for an AI and a task that deserve that trust. The tool starts commands in the workspace folder unless the AI gives a working directory.
    /// </summary>
    bool IsShellAllowed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI's file tools only accept paths inside the workspace, <see langword="true" /> by default. A path that leads outside, absolute or relative and also through a link, is refused, and the workspace is only where relative paths start when it is <see langword="false" />, in which case the AI can also use any path on the machine. The workspace is <see cref="IPromptSession.WorkspacePath" />, and the cache (<c>CACHE:</c>) refuses paths that leave it either way. It applies to every tool that reads or writes files, <c>view_image</c> included, and not to the shell, which is not confined by anything. It is stored with the session.
    /// </summary>
    bool IsSandboxed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI can use the web: read a web page or other text from an http or https address with a tool (<c>fetch_url</c>), as this machine and with its network access (addresses on the local network and on this machine included), and search the web. When the AI's tool has a web search of its own (Claude Code and Codex do) the AI is allowed to use it, and the library does not see or filter what it searches or reads there; otherwise the AI is given a tool, <c>search_web</c>, that asks DuckDuckGo's plain HTML page, which needs no key and no other software but is best effort: the page is read, not an API, so it can change or turn the library away, and the library spaces its requests at least two seconds apart and stops for a minute when it is turned away. Nothing restricts what the AI reads or searches. It is <see langword="false" /> by default and stored with the session, and changing it ends a kept process of a tool that takes its web search when it starts.
    /// </summary>
    bool IsWebAllowed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI can make images, with the service the environment names (<see cref="PromptEnvironment.ImageGeneration" />, an <see cref="OpenAiImageSetup" /> or a <see cref="LocalImageSetup" />). It is <see langword="false" /> by default. While it is <see langword="true" /> and the environment has an image generation setup, the AI is offered <c>generate_image</c>, which saves each image as a file where the AI says and never replaces a file, and only what the service supports; without a setup in the environment the AI has no such tool. The model is the library's choice. Images can cost money or take a graphics card for minutes, so the AI is told to ask for only what it needs. The setup is read from the environment when a prompt starts, so the key and the service are never stored with the session, only this switch is, and the name <c>generate_image</c> is taken.
    /// </summary>
    bool IsImageGenerationAllowed { get; set; }

    /// <summary>
    /// Gets or sets custom instructions for the AI, added to the library's own guidance for every prompt of the session, or <see langword="null" /> for none; blank instructions mean none. They can be changed at any time and the next prompt follows the current ones: Claude Code renders its system prompt again when it resumes, and for Codex, which keeps the instructions a conversation started with, the library adds the new ones to the conversation as a developer message with the next prompt.
    /// </summary>
    string? Instructions { get; set; }
}
