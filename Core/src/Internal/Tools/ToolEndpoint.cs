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
    /// Gets the first part of a path in the session storage (<c>.liveai-session</c>), which the AI is told about.
    /// </summary>
    public string SessionName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the first part of a path in the session's cache (<c>.liveai-cache</c>), which the AI is told about.
    /// </summary>
    public string CacheName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the folder in the workspace for data that commands use, or an empty text when the workspace cannot have one (a folder of that name that the library did not make is there), which the AI is told about.
    /// </summary>
    public string WorkspaceTempName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the tool that downloads files, or an empty text when the tools do not include it.
    /// </summary>
    public string DownloadName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the most bytes a file in the session storage or the cache may have, which the AI is told about, or zero for no statement.
    /// </summary>
    public long FileLimit { get; init; }

    /// <summary>
    /// Gets how long an unused file in the cache and the workspace folder is kept, which the AI is told about, or zero for no statement.
    /// </summary>
    public TimeSpan Retention { get; init; }

    /// <summary>
    /// Gets a value indicating whether the tools include the shell tool, which decides what the AI is told about its access.
    /// </summary>
    public bool HasShell { get; init; }

    /// <inheritdoc />
    public bool Equals(ToolEndpoint? other)
        => other is not null && Url == other.Url && Token == other.Token && HasShell == other.HasShell && WorkspaceTempName == other.WorkspaceTempName && FileLimit == other.FileLimit && Retention == other.Retention && Names.SequenceEqual(other.Names);

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
    {
        string limit = endpoint.FileLimit > 0 ? $", with files of up to {endpoint.FileLimit.DescribeSize()} each" : string.Empty;
        string expiry = endpoint.Retention > TimeSpan.Zero ? $"a file in it that you did not read or write for {endpoint.Retention.DescribeDuration()} is removed" : "a file in it that you did not read or write for a while is removed";
        string download = endpoint.DownloadName.Length > 0 ? $" Save files from the web with {endpoint.DownloadName}, which puts them in {endpoint.CacheName}/downloads unless you give a path." : string.Empty;

        return $"A path you give the file tools is relative to your workspace, your working directory, which is kept for the long term, unless it starts with {endpoint.SessionName}, fast storage that lasts as long as this session{limit} (for example {endpoint.SessionName}/notes.txt), or {endpoint.CacheName}, which is the same except that {expiry}; list_directory with no path shows the workspace, {endpoint.SessionName} and {endpoint.CacheName}. Only the file tools reach {endpoint.SessionName} and {endpoint.CacheName}: to keep something from them, copy it into the workspace with copy_file.{download}";
    }

    private static string Commands(ToolEndpoint endpoint)
    {
        string folder = endpoint.WorkspaceTempName.Length > 0 ? $" For data that only commands need, use the folder {endpoint.WorkspaceTempName} in the workspace: what is in it that was not used for {(endpoint.Retention > TimeSpan.Zero ? endpoint.Retention.DescribeDuration() : "a while")} is removed (running a command counts as using all of it), and the folder is removed when the session ends." : string.Empty;
        return $" The commands you run reach only the workspace, not {endpoint.SessionName} or {endpoint.CacheName}, so copy a file into the workspace before a command uses it.{folder}";
    }

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
                { HasShell: true } => $"You have a shell tool and file tools, both in the MCP server named {tools.ServerName}, and no other way to reach this machine. The shell tool starts in your workspace. {Paths(tools)}{Commands(tools)}{external}",
                _ => $"You have no shell and no direct access to this machine. Your only way to work with files is the file tools of the MCP server named {tools.ServerName}. {Paths(tools)}{external}"
            };

            access = record.NativeSearch ? $"{access} You can also search the web with your own web search tool." : access;

            return string.IsNullOrWhiteSpace(record.Instructions) ? access : $"{access}\n\nInstructions from the user:\n{record.Instructions}";
        }
    }

}
