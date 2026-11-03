namespace Markwardt.LiveAi;

/// <summary>
/// The settings of a session at one moment, which a prompt takes when it starts and keeps for its whole run.
/// </summary>
internal sealed record PromptSessionSettingsSnapshot
{
    /// <summary>
    /// Gets the selected model, or <see langword="null" /> when none is.
    /// </summary>
    public PromptModel? Model { get; init; }

    /// <summary>
    /// Gets the values of the model's settings.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ModelSettings { get; init; }

    /// <summary>
    /// Gets a value indicating whether the process stays alive between prompts.
    /// </summary>
    public required bool IsPersistent { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI has a shell tool.
    /// </summary>
    public required bool EnableShell { get; init; }

    /// <summary>
    /// Gets the function that decides which tool calls may run, or <see langword="null" /> for all.
    /// </summary>
    public Func<PromptToolCallEvent, CancellationToken, Task<PromptToolDecision>>? BeforeToolCall { get; init; }

    /// <summary>
    /// Gets the function that is told what became of each tool call, or <see langword="null" /> for none.
    /// </summary>
    public Func<PromptToolCallEvent, PromptToolResultEvent, TimeSpan, CancellationToken, Task>? AfterToolCall { get; init; }

    /// <summary>
    /// Gets the limits of the session's requests.
    /// </summary>
    /// <summary>
    /// Gets the function that answers the questions of the AI, or <see langword="null" /> when the AI is offered no tool to ask.
    /// </summary>
    public Func<string, CancellationToken, Task<string>>? Asker { get; init; }

    public PromptSessionLimits Limits { get; init; } = new();

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
    /// Gets the mounted stores by name.
    /// </summary>
    public required IReadOnlyDictionary<string, IStore> Mounts { get; init; }

    /// <summary>
    /// Gets the store that relative paths are relative to, or <see langword="null" /> for none.
    /// </summary>
    public IStore? Workspace { get; init; }

    /// <summary>
    /// Sets every setting of a configuration to what this snapshot holds, replacing its tools, mounts and external servers.
    /// </summary>
    /// <param name="target">The configuration to set.</param>
    public void CopyTo(IPromptSessionConfiguration target)
    {
        target.Model = Model;
        target.ModelSettings.Clear();

        foreach ((string id, string value) in ModelSettings)
        {
            target.ModelSettings[id] = value;
        }

        target.IsPersistent = IsPersistent;
        target.EnableShell = EnableShell;
        target.Workspace = Workspace;
        target.Instructions = Instructions;
        target.BeforeToolCall = BeforeToolCall;
        target.AfterToolCall = AfterToolCall;
        target.Asker = Asker;
        target.Limits = Limits;
        target.Mounts.Clear();
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

        foreach ((string name, IStore store) in Mounts)
        {
            target.Mounts[name] = store;
        }
    }
}
