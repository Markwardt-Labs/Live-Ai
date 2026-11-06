namespace Markwardt.LiveAi;

/// <summary>
/// Starts local MCP servers that offer tools to the AI's tool.
/// </summary>
internal interface IPromptToolHost
{
    /// <summary>
    /// Starts a server on a free loopback port.
    /// </summary>
    /// <param name="tools">The tools to offer.</param>
    /// <returns>The running server, to be disposed when the tools are no longer offered.</returns>
    /// <exception cref="PromptSessionException">No port could be listened on.</exception>
    IPromptToolServer Start(IReadOnlyList<PromptTool> tools);
}

/// <inheritdoc />
internal sealed class PromptToolHost : IPromptToolHost
{
    /// <inheritdoc />
    public IPromptToolServer Start(IReadOnlyList<PromptTool> tools)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            PromptToolServer server = new(tools, listener, new ToolEndpoint { Url = $"http://127.0.0.1:{port}/mcp", Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), Names = tools.Select(tool => tool.Name).ToArray() });
            server.Start();
            return server;
        }

        throw new PromptSessionException("No local port was free to offer the tools on.");
    }
}
