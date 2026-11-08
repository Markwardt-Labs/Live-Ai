namespace Markwardt.LiveAi;

/// <summary>
/// How things run in the environment the library is used in, as opposed to what a session is created with: where each tool is, where it keeps its configuration and the API key that new sessions use by default, the server, models and context sizes of the local models, the service that makes images, and the configuration that new sessions start with. It belongs to the <see cref="IPrompter" /> and applies to all of its sessions. It is not stored with them: a session that is loaded runs with the environment of the prompter that loads it (a folder or command that moved with the machine needs no change to the sessions), except for the API key and the starting configuration, which a session copies when it is created and keeps.
/// </summary>
public sealed record PromptEnvironment
{
    /// <summary>
    /// Gets the command that starts Claude Code: <c>claude</c> found on the <c>PATH</c>, or the path of another one.
    /// </summary>
    public string ClaudeCommand { get; init; } = "claude";

    /// <summary>
    /// Gets the folder Claude Code keeps its configuration and sessions in, or <see langword="null" /> for its usual one.
    /// </summary>
    public string? ClaudeConfigFolder { get; init; }

    /// <summary>
    /// Gets the API key that new Claude Code sessions and tests use unless their setup has a key of its own (<see cref="ClaudeSetup.ApiKey" />), or <see langword="null" /> for the subscription already signed in to in <c>claude</c>. A session stores the key it was created with, in plain text like any other.
    /// </summary>
    public string? ClaudeApiKey { get; init; }

    /// <summary>
    /// Gets the command that starts Codex: <c>codex</c> found on the <c>PATH</c>, or the path of another one.
    /// </summary>
    public string CodexCommand { get; init; } = "codex";

    /// <summary>
    /// Gets the folder Codex keeps its configuration and sessions in, or <see langword="null" /> for its usual one.
    /// </summary>
    public string? CodexConfigFolder { get; init; }

    /// <summary>
    /// Gets the API key that new Codex sessions and tests use unless their setup has a key of its own (<see cref="CodexSetup.ApiKey" />), or <see langword="null" /> for the sign in already configured in <c>codex</c>. A session stores the key it was created with, in plain text like any other.
    /// </summary>
    public string? CodexApiKey { get; init; }

    /// <summary>
    /// Gets the command that starts OpenCode, the tool that runs the local models: <c>opencode</c> found on the <c>PATH</c>, or the path of another one.
    /// </summary>
    public string LocalCommand { get; init; } = "opencode";

    /// <summary>
    /// Gets the server, the model of each capability and the size of each context window that local sessions use unless a session overrides them (<see cref="LocalSetup.Overrides" />). The server is Ollama's usual address (<see cref="PromptServerSettings.DefaultServer" />) by default, and the models are not set. A session fails to be created when neither this nor the session names the models, since which model to run is the one thing the library cannot know about a local server.
    /// </summary>
    public PromptServerSettings LocalSettings { get; init; } = new() { Server = PromptServerSettings.DefaultServer };

    /// <summary>
    /// Gets the service that makes the images the AI asks for and how it is reached (an <see cref="OpenAiImageSetup" /> or a <see cref="LocalImageSetup" />, with its API key if it has one), or <see langword="null" /> for none, which is the default. Only the sessions that allow it (<see cref="IPromptSessionConfiguration.IsImageGenerationAllowed" />) are offered the <c>generate_image</c> tool. It is read when a prompt starts, so the key lives in the environment and is not stored with the sessions. A setup that is not valid (an empty API key, say) only fails the sessions that allow image generation.
    /// </summary>
    public PromptImageSetup? ImageGeneration { get; init; }

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.Capability" /> has in a session that is created, <see cref="PromptCapability.Balanced" /> by default. Sessions keep the value they were created with, whatever this is set to later.
    /// </summary>
    public PromptCapability Capability { get; init; } = PromptCapability.Balanced;

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.Effort" /> has in a session that is created, <see cref="PromptEffort.High" /> by default.
    /// </summary>
    public PromptEffort Effort { get; init; } = PromptEffort.High;

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.IsShellAllowed" /> has in a session that is created, <see langword="false" /> by default.
    /// </summary>
    public bool IsShellAllowed { get; init; }

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.IsSystemAccessAllowed" /> has in a session that is created, <see langword="false" /> by default.
    /// </summary>
    public bool IsSystemAccessAllowed { get; init; }

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.IsWebAllowed" /> has in a session that is created, <see langword="false" /> by default.
    /// </summary>
    public bool IsWebAllowed { get; init; }

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.IsImageGenerationAllowed" /> has in a session that is created, <see langword="false" /> by default. It takes effect with the image generation of the environment (<see cref="ImageGeneration" />).
    /// </summary>
    public bool IsImageGenerationAllowed { get; init; }

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.Instructions" /> has in a session that is created, <see langword="null" /> (none) by default.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Gets the value <see cref="IPromptSessionConfiguration.Storage" /> has in a session that is created: how much its session storage and cache hold in memory, the largest single file, and how long unused files are kept. The defaults are sensible for most uses.
    /// </summary>
    public PromptStorageSettings Storage { get; init; } = new();

    /// <summary>
    /// Describes the members for <see cref="object.ToString" />, with the API keys hidden, so they do not end up in a log or a failure message.
    /// </summary>
    /// <param name="builder">Receives the text.</param>
    /// <returns><see langword="true" />, since members were written.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ClaudeCommand = ").Append(ClaudeCommand);
        builder.Append(", ClaudeConfigFolder = ").Append(ClaudeConfigFolder);
        builder.Append(", ClaudeApiKey = ").Append(ClaudeApiKey is null ? string.Empty : "***");
        builder.Append(", CodexCommand = ").Append(CodexCommand);
        builder.Append(", CodexConfigFolder = ").Append(CodexConfigFolder);
        builder.Append(", CodexApiKey = ").Append(CodexApiKey is null ? string.Empty : "***");
        builder.Append(", LocalCommand = ").Append(LocalCommand);
        builder.Append(", LocalSettings = ").Append(LocalSettings);
        builder.Append(", ImageGeneration = ").Append(ImageGeneration);
        builder.Append(", Capability = ").Append(Capability);
        builder.Append(", Effort = ").Append(Effort);
        builder.Append(", IsShellAllowed = ").Append(IsShellAllowed);
        builder.Append(", IsSystemAccessAllowed = ").Append(IsSystemAccessAllowed);
        builder.Append(", IsWebAllowed = ").Append(IsWebAllowed);
        builder.Append(", IsImageGenerationAllowed = ").Append(IsImageGenerationAllowed);
        builder.Append(", Instructions = ").Append(Instructions);
        builder.Append(", Storage = ").Append(Storage);
        return true;
    }
}
