namespace Markwardt.LiveAi;

/// <summary>
/// What a session is made, loaded or moved with, everything optional. Each call uses the part that means something to it and refuses the part that does not: <see cref="IPromptProvider.Create" /> uses all of it, <see cref="IPromptProvider.Load" /> only the API key (the workspace is stored with the session, and <see cref="IPromptSession.AttachTo" /> changes it), and <see cref="IPromptSession.AttachTo" /> only the workspace (the session keeps its API key).
/// </summary>
public sealed record PromptSessionOptions
{
    /// <summary>
    /// Gets the API key to run with, or <see langword="null" /> to use the subscription already configured in the command line tool. A key is never stored, so it is given again on every load.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the model the session runs, one of the models <see cref="IPromptProvider.GetModels" /> lists, or <see langword="null" /> to select one later with <see cref="IPromptSessionConfiguration.Model" /> (a loaded session then starts with the model it was stored with). The settings of the model start at their defaults; a loaded session keeps the values of the settings that both models have.
    /// </summary>
    public PromptModel? Model { get; init; }

    /// <summary>
    /// Gets the folder on disk that is the session's workspace, or <see langword="null" /> for none. The session's <see cref="IPromptSessionConfiguration.Workspace" /> is set to it and the tool runs in it, which is the folder a Claude Code conversation is found by; without a workspace the tool runs in an empty folder of its own. The folder must exist.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI's file tools only accept paths inside <see cref="WorkspacePath" />, as the <c>sandbox</c> argument of <c>IStore.FromDisk</c>. It is <see langword="true" /> by default and is ignored without a workspace path.
    /// </summary>
    public bool Sandbox { get; init; } = true;
}
