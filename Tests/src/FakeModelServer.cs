namespace Markwardt.LiveAi.Tests;

/// <summary>
/// An in-process stand-in for a server of local models: it answers each path with the body a test gives it (and <c>404</c> for any other), and keeps the paths it was asked for.
/// </summary>
public sealed class FakeModelServer : IDisposable
{
    private readonly HttpListener listener = new();

    private readonly Dictionary<string, (int Status, string Body)> routes = [];

    private readonly List<string> requested = [];

    private readonly object gate = new();

    private readonly Task serving;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeModelServer" /> class and starts listening on a free port.
    /// </summary>
    public FakeModelServer()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Root = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add($"{Root}/");
        listener.Start();
        serving = Serve();
    }

    /// <summary>
    /// Gets the address of the server, without a path.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Gets the address of the OpenAI compatible API of the server.
    /// </summary>
    public string Api => $"{Root}/v1";

    /// <summary>
    /// Gets the paths the server was asked for, in order.
    /// </summary>
    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (gate)
            {
                return [.. requested];
            }
        }
    }

    /// <summary>
    /// Makes the server answer a path.
    /// </summary>
    /// <param name="path">The path, such as <c>/api/tags</c>.</param>
    /// <param name="body">The JSON to answer with.</param>
    /// <param name="status">The status code to answer with.</param>
    /// <returns>This server.</returns>
    public FakeModelServer Answer(string path, string body, int status = 200)
    {
        lock (gate)
        {
            routes[path] = (status, body);
        }

        return this;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        listener.Close();
        serving.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            (int Status, string Body) answer;

            lock (gate)
            {
                requested.Add(context.Request.Url!.AbsolutePath);
                answer = routes.GetValueOrDefault(context.Request.Url.AbsolutePath, (404, "{}"));
            }

            byte[] body = Encoding.UTF8.GetBytes(answer.Body);
            context.Response.StatusCode = answer.Status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }
}
