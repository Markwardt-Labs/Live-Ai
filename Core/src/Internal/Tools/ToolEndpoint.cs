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
    /// Gets the first part of a path in the session's cache (<c>CACHE:</c>), which the AI is told about.
    /// </summary>
    public string CacheName { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the tools include the shell tool, which decides what the AI is told about its access.
    /// </summary>
    public bool HasShell { get; init; }

    /// <inheritdoc />
    public bool Equals(ToolEndpoint? other)
        => other is not null && Url == other.Url && Token == other.Token && HasShell == other.HasShell && Names.SequenceEqual(other.Names);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Url, Token);
}

/// <summary>
/// What the AI is told about its tools.
/// </summary>
internal static class ToolEndpointExtensions
{
    private static string Paths(ToolEndpoint endpoint)
        => $"A path you give the file tools is relative to your workspace, your working directory, unless it starts with {endpoint.CacheName}, the cache: temporary storage for this session that is lost when the session ends (for example {endpoint.CacheName}/notes.txt); list_directory with no path shows the workspace and {endpoint.CacheName}.";

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
                { HasShell: true } => $"You have a shell tool and file tools, both in the MCP server named {tools.ServerName}, and no other way to reach this machine. The shell tool starts in your workspace. {Paths(tools)}{external}",
                _ => $"You have no shell and no direct access to this machine. Your only way to work with files is the file tools of the MCP server named {tools.ServerName}. {Paths(tools)}{external}"
            };

            access = record.NativeSearch ? $"{access} You can also search the web with your own web search tool." : access;

            return string.IsNullOrWhiteSpace(record.Instructions) ? access : $"{access}\n\nInstructions from the user:\n{record.Instructions}";
        }
    }

}
