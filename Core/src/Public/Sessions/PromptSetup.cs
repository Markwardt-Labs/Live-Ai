namespace Markwardt.LiveAi;

/// <summary>
/// What a session is created with: which tool runs it, how it signs in, and the configuration it starts with. It holds what every tool has in common, the workspace and the starting values of the session's configuration; each tool has a type that adds just what that tool needs, and only the library derives from it: <see cref="ClaudeSetup" />, <see cref="CodexSetup" /> and <see cref="LocalSetup" />. The tool-specific part of the setup is stored with the session (the API key too), so loading or deleting it needs only its identifier; where the tools are is the <see cref="PromptEnvironment" /> of the prompter. What can change while a session lives is on its <see cref="IPromptSession.Configuration" />.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "tool")]
[JsonDerivedType(typeof(ClaudeSetup), "claude")]
[JsonDerivedType(typeof(CodexSetup), "codex")]
[JsonDerivedType(typeof(LocalSetup), "local")]
public abstract record PromptSetup
{
    private protected PromptSetup()
    {
    }

    /// <summary>
    /// Gets the full path of an existing folder the session works in, or <see langword="null" /> for a folder the library makes for the session in its data folder (named after the session, so the files the AI writes there are kept until the session is deleted or moved elsewhere). The tool runs in it, so it finds the instruction files of the project (such as <c>CLAUDE.md</c> and <c>AGENTS.md</c>) as when started there, and the AI's file tools take relative paths relative to it (see <see cref="IPromptSession.WorkspacePath" />). It is stored with the session, so a load brings it back; <see cref="IPromptSession.Move" /> changes it. A folder given here is never deleted.
    /// </summary>
    [JsonIgnore]
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Gets the starting value of <see cref="IPromptSessionConfiguration.Capability" />, <see cref="PromptCapability.Balanced" /> by default. It is used when the session is created and not stored with the setup: the configuration of the session is, and a loaded session has the values it was left with.
    /// </summary>
    [JsonIgnore]
    public PromptCapability Capability { get; init; } = PromptCapability.Balanced;

    /// <summary>
    /// Gets the starting value of <see cref="IPromptSessionConfiguration.Effort" />, <see cref="PromptEffort.Medium" /> by default. It is used when the session is created and not stored with the setup.
    /// </summary>
    [JsonIgnore]
    public PromptEffort Effort { get; init; } = PromptEffort.Medium;

    /// <summary>
    /// Gets the starting value of <see cref="IPromptSessionConfiguration.IsShellAllowed" />, <see langword="false" /> by default. It is used when the session is created and not stored with the setup.
    /// </summary>
    [JsonIgnore]
    public bool IsShellAllowed { get; init; }

    /// <summary>
    /// Gets the starting value of <see cref="IPromptSessionConfiguration.IsSandboxed" />, <see langword="true" /> by default. It is used when the session is created and not stored with the setup.
    /// </summary>
    [JsonIgnore]
    public bool IsSandboxed { get; init; } = true;

    /// <summary>
    /// Gets the starting value of <see cref="IPromptSessionConfiguration.IsWebAllowed" />, <see langword="false" /> by default. It is used when the session is created and not stored with the setup.
    /// </summary>
    [JsonIgnore]
    public bool IsWebAllowed { get; init; }

    /// <summary>
    /// Gets the starting value of <see cref="IPromptSessionConfiguration.IsImageGenerationAllowed" />, <see langword="false" /> by default. It is used when the session is created and not stored with the setup.
    /// </summary>
    [JsonIgnore]
    public bool IsImageGenerationAllowed { get; init; }

    /// <summary>
    /// Gets the starting value of <see cref="IPromptSessionConfiguration.Instructions" />, <see langword="null" /> by default. It is used when the session is created and not stored with the setup.
    /// </summary>
    [JsonIgnore]
    public string? Instructions { get; init; }
}
