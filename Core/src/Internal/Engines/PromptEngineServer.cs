namespace Markwardt.LiveAi;

/// <summary>
/// An MCP server that an engine's tool must be told about: the library's own server for the file tools, the shell and the caller's tools, or one of the external servers of the session.
/// </summary>
internal sealed record PromptEngineServer
{
    /// <summary>
    /// Gets the name to register the server under, which prefixes its tools in most tools. It is made of letters, digits, underscores and hyphens.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the address of an HTTP server, or <see langword="null" /> for a program the tool starts.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets the name of the environment variable that holds the bearer token the HTTP server wants, or <see langword="null" /> for none.
    /// </summary>
    public string? BearerTokenVariable { get; init; }

    /// <summary>
    /// Gets the value the engine must put in <see cref="BearerTokenVariable" /> in the environment of the process it starts, or <see langword="null" /> when the variable is already in the environment of the host (an external server's token). It is a secret: it belongs in the environment and never on a command line.
    /// </summary>
    public string? BearerToken { get; init; }

    /// <summary>
    /// Gets the program to start for a server that talks over its standard input and output, or <see langword="null" /> for an HTTP server.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Gets the arguments of <see cref="Command" />.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets environment variables for <see cref="Command" />.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the names of the tools the server offers when they are known and the tool must be told which it may use (the library's own server), or <see langword="null" /> when all are to be allowed (an external server).
    /// </summary>
    public IReadOnlyList<string>? Tools { get; init; }
}
