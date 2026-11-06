namespace Markwardt.LiveAi;

/// <summary>
/// The settings of a session at one moment, which a prompt takes when it starts and keeps for its whole run.
/// </summary>
internal sealed record PromptSessionSettingsSnapshot
{
    /// <summary>
    /// Gets how capable the model is.
    /// </summary>
    public required PromptCapability Capability { get; init; }

    /// <summary>
    /// Gets how much effort the model puts into each prompt.
    /// </summary>
    public required PromptEffort Effort { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI has a shell tool.
    /// </summary>
    public required bool IsShellAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the workspace only accepts paths inside its folder.
    /// </summary>
    public required bool IsSandboxed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI has a tool to fetch web pages.
    /// </summary>
    public required bool IsWebAllowed { get; init; }

    /// <summary>
    /// Gets how images are made for the AI, or <see langword="null" /> when the AI has no tool for it.
    /// </summary>
    public PromptImageOptions? ImageGeneration { get; init; }

    /// <summary>
    /// Gets the function that answers the questions of the AI, or <see langword="null" /> when the AI is offered no tool to ask.
    /// </summary>
    public PromptAsker? Asker { get; init; }

    /// <summary>
    /// Gets the custom instructions, or <see langword="null" /> for none.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Gets the external MCP servers by name.
    /// </summary>
    public required IReadOnlyDictionary<string, PromptMcpServer> McpServers { get; init; }

    /// <summary>
    /// Gets the caller's tools.
    /// </summary>
    public required IReadOnlyList<PromptTool> Tools { get; init; }

    /// <summary>
    /// Sets every setting to what this snapshot holds, replacing the tools and external servers.
    /// </summary>
    /// <param name="target">The settings to set.</param>
    public void CopyTo(PromptSessionSettings target)
    {
        target.Capability = Capability;
        target.Effort = Effort;
        target.IsShellAllowed = IsShellAllowed;
        target.IsSandboxed = IsSandboxed;
        target.IsWebAllowed = IsWebAllowed;
        target.Instructions = Instructions;
        target.ImageGeneration = ImageGeneration;
        target.Asker = Asker;
        target.McpServers.Clear();
        target.Tools.Clear();

        foreach ((string name, PromptMcpServer server) in McpServers)
        {
            target.McpServers[name] = server;
        }

        foreach (PromptTool tool in Tools)
        {
            target.Tools.Add(tool);
        }
    }
}
