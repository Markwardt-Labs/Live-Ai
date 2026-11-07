namespace Markwardt.LiveAi;

/// <summary>
/// What a session is created with: which tool runs it, how it signs in, and where it works. It holds what every tool has in common, the workspace; each tool has a type that adds just what that tool needs, and only the library derives from it: <see cref="ClaudeSetup" />, <see cref="CodexSetup" /> and <see cref="LocalSetup" />. The tool-specific part of the setup is stored with the session (the API key too), so loading or deleting it needs only its identifier; where the tools are is the <see cref="PromptEnvironment" /> of the prompter. What can change while a session lives is on its <see cref="IPromptSession.Configuration" />.
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

}
