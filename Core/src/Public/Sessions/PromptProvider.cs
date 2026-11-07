namespace Markwardt.LiveAi;

/// <summary>
/// Which provider a session runs on, which is what the type of the <see cref="PromptSetup" /> it was created with chose.
/// </summary>
public enum PromptProvider
{
    /// <summary>
    /// Claude Code (<see cref="ClaudeSetup" />).
    /// </summary>
    Claude,

    /// <summary>
    /// OpenAI Codex (<see cref="CodexSetup" />).
    /// </summary>
    Codex,

    /// <summary>
    /// A model served by a model server on this machine or the network, run through OpenCode (<see cref="LocalSetup" />).
    /// </summary>
    Local
}
