namespace Markwardt.LiveAi;

/// <summary>
/// Where an engine's tool reaches the caller's tools: a local MCP server and the secret that opens it.
/// </summary>
internal sealed record ToolEndpoint
{
    /// <summary>
    /// Gets the name the tools are registered under in the tool, which prefixes their names there.
    /// </summary>
    public string ServerName => "liveai";

    /// <summary>
    /// Gets the name of the environment variable that carries <see cref="Token" /> to the tool.
    /// </summary>
    public string TokenVariable => "LIVEAI_TOOLS_TOKEN";

    /// <summary>
    /// Gets the address of the MCP server.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the secret the tool presents to the server as a bearer token.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// Gets the names of the tools the server offers.
    /// </summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>
    /// Gets a value indicating whether the tools include the library's file tools, which decides what the AI is told about its access.
    /// </summary>
    public bool HasFiles { get; init; }

    /// <summary>
    /// Gets a value indicating whether the tools include the shell tool, which decides what the AI is told about its access.
    /// </summary>
    public bool HasShell { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file tools have a workspace that relative paths are relative to, which decides what the AI is told about its paths.
    /// </summary>
    public bool HasWorkspace { get; init; }

    /// <summary>
    /// Gets a value indicating whether the shell starts in the workspace and not in an empty scratch folder, which decides what the AI is told about it.
    /// </summary>
    public bool ShellInWorkspace { get; init; }

    /// <inheritdoc />
    public bool Equals(ToolEndpoint? other)
        => other is not null && Url == other.Url && Token == other.Token && HasFiles == other.HasFiles && HasShell == other.HasShell && HasWorkspace == other.HasWorkspace && ShellInWorkspace == other.ShellInWorkspace && Names.SequenceEqual(other.Names);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Url, Token);
}

/// <summary>
/// What the AI is told about its tools.
/// </summary>
internal static class ToolEndpointExtensions
{
    extension(ToolEndpoint? tools)
    {
        /// <summary>
        /// Gets the instructions that tell the AI how it can work, which keeps it from looking for files and programs it has no way to reach, followed by the custom instructions of the session.
        /// </summary>
        /// <param name="record">The session, whose external MCP servers and instructions are told about.</param>
        /// <returns>The text.</returns>
        public string Guidance(PromptSessionRecord record)
        {
            string external = record.McpServers.Count > 0 ? $" You may also use the tools of the MCP servers named {string.Join(", ", record.McpServers.Keys)}." : string.Empty;
            string access = tools switch
            {
                null when record.McpServers.Count == 0 => "You have no tools: you cannot read or change files or run programs, so answer from what you know.",
                null => $"You have no access to files of your own and cannot run programs.{external} Use only those tools.",
                { HasFiles: false, HasShell: false } => $"You have no access to files and cannot run programs. Use only the tools of the MCP server named {tools.ServerName}.{external}",
                { HasFiles: false } => $"You can run commands on this machine only with the shell tool of the MCP server named {tools.ServerName}, which starts in an empty scratch folder, so give it a working_directory or use absolute paths. You have no other access to files. Use only the tools of that MCP server.{external}",
                { HasShell: true } => $"You have a shell tool and file tools, both in the MCP server named {tools.ServerName}, and no other way to reach this machine. {(tools.ShellInWorkspace ? "The shell tool starts in your workspace." : "The shell tool starts in an empty scratch folder that is not your workspace.")} {Paths(tools)}{external}",
                _ => $"You have no shell and no direct access to this machine{(record.WorkspacePath is null ? ", and the folder you run in is empty and is not your workspace" : string.Empty)}. Your only way to work with files is the file tools of the MCP server named {tools.ServerName}. {Paths(tools)}{external}"
            };

            return string.IsNullOrWhiteSpace(record.Instructions) ? access : $"{access}\n\nInstructions from the user:\n{record.Instructions}";
        }
    }

    private static string Paths(ToolEndpoint endpoint)
        => endpoint.HasWorkspace
            ? "A path you give the file tools is relative to your workspace, your working directory, unless it starts with the path of a mounted folder, which it is then inside; list_directory with no path shows the workspace."
            : "Every path you give the file tools starts with the path of a mounted folder; list_directory with no path shows the mounted folders.";
}
