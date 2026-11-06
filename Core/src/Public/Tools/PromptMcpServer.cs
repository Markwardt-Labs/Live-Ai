namespace Markwardt.LiveAi;

/// <summary>
/// An external MCP server whose tools the AI can use: a program the tool starts (<see cref="Stdio" />) or one that is reached over HTTP (<see cref="Http" />). Put no secret in it: name an environment variable with <see cref="BearerTokenVariable" /> instead, which the host process must have set.
/// </summary>
public sealed record PromptMcpServer
{
    /// <summary>
    /// Describes a server that is a program talking over its standard input and output.
    /// </summary>
    /// <param name="command">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The server.</returns>
    public static PromptMcpServer Stdio(string command, params string[] arguments)
        => new() { Command = command, Arguments = arguments };

    /// <summary>
    /// Describes a server that is reached over HTTP.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="bearerTokenVariable">The name of the environment variable that holds the bearer token, or <see langword="null" /> for none.</param>
    /// <returns>The server.</returns>
    public static PromptMcpServer Http(string url, string? bearerTokenVariable = null)
        => new() { Url = url, BearerTokenVariable = bearerTokenVariable };

    /// <summary>
    /// Gets the program to start for a server that talks over its standard input and output, or <see langword="null" /> for an HTTP server.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Gets the arguments of <see cref="Command" />.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets environment variables set for <see cref="Command" />, besides the ones the tool passes on.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the address of an HTTP server, or <see langword="null" /> for a program the tool starts.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets the name of an environment variable that holds the bearer token the HTTP server wants, or <see langword="null" /> for none.
    /// </summary>
    public string? BearerTokenVariable { get; init; }

    /// <inheritdoc />
    public bool Equals(PromptMcpServer? other)
        => other is not null && Command == other.Command && Url == other.Url && BearerTokenVariable == other.BearerTokenVariable && Arguments.SequenceEqual(other.Arguments) && EnvironmentVariables.Count == other.EnvironmentVariables.Count && EnvironmentVariables.All(entry => other.EnvironmentVariables.TryGetValue(entry.Key, out string? value) && value == entry.Value);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Command, Url, BearerTokenVariable, Arguments.Count);
}
