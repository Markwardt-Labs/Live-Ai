namespace Markwardt.LiveAi.Tests;

/// <summary>
/// An in-process stand-in for the HTTP server of <c>opencode serve</c>: it answers the requests the library makes, keeps them, and sends the events a test scripts for each prompt over its stream of server-sent events.
/// </summary>
public sealed class FakeOpenCodeServer : IAsyncDisposable
{
    private readonly HttpListener listener = new();

    private readonly CancellationTokenSource stopping = new();

    private readonly List<StreamWriter> streams = [];

    private readonly List<Recorded> recorded = [];

    private readonly object gate = new();

    private readonly Task serving;

    private TaskCompletionSource interrupt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource steer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int polls;

    private int created;

    private int events;

    private bool running;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeOpenCodeServer" /> class and starts listening on a free port.
    /// </summary>
    public FakeOpenCodeServer()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add($"{Url}/");
        listener.Start();
        serving = Task.Run(Serve);
    }

    /// <summary>
    /// Gets the address the server listens on.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Gets or sets the identifier of the session that <c>POST /api/session</c> creates.
    /// </summary>
    public string SessionId { get; set; } = "ses_fake";

    /// <summary>
    /// Gets or sets the identifiers of the sessions that already exist, besides the ones created.
    /// </summary>
    public IReadOnlyList<string> KnownSessions { get; set; } = [];

    /// <summary>
    /// Gets or sets the status of the MCP server called <c>liveai</c> for each poll of <c>/api/mcp</c>; the last one repeats. An empty list means that the server is not listed.
    /// </summary>
    public IReadOnlyList<string> McpStatuses { get; set; } = ["connected"];

    /// <summary>
    /// Gets or sets the names of the MCP servers that are listed besides <c>liveai</c>, which are connected.
    /// </summary>
    public IReadOnlyList<string> OtherMcpServers { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether asking to connect a failed MCP server makes it connected.
    /// </summary>
    public bool ConnectRecovers { get; set; } = true;

    /// <summary>
    /// Gets or sets the function that scripts the events of a prompt from the body of its request.
    /// </summary>
    public Func<JsonElement, IReadOnlyList<Step>> OnPrompt { get; set; } = _ => [];

    /// <summary>
    /// Gets the requests the server was sent, in order.
    /// </summary>
    public IReadOnlyList<Recorded> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. recorded];
            }
        }
    }

    /// <summary>
    /// Finds the bodies of the requests with a method and a path.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path.</param>
    /// <returns>The bodies, in order.</returns>
    public IReadOnlyList<JsonElement> Bodies(string method, string path)
        => [.. Requests.Where(request => request.Method == method && request.Path == path).Select(request => request.Body)];

    /// <summary>
    /// Makes the steps of a plain answer: the execution starts, the text is written in two pieces and ends, and the execution succeeds.
    /// </summary>
    /// <param name="text">The answer.</param>
    /// <returns>The steps.</returns>
    public IReadOnlyList<Step> Reply(string text)
        =>
        [
            Step.Event("session.execution.started", new { }),
            Step.Event("session.step.started", new { agent = "build" }),
            Step.Event("session.text.started", new { ordinal = 0 }),
            Step.Event("session.text.delta", new { ordinal = 0, delta = text[..(text.Length / 2)] }),
            Step.Event("session.text.delta", new { ordinal = 0, delta = text[(text.Length / 2)..] }),
            Step.Event("session.text.ended", new { ordinal = 0, text }),
            Step.Event("session.step.ended", new { finish = "stop", tokens = new { input = 10, output = 5, reasoning = 0, cache = new { read = 0, write = 0 } }, cost = 0 }),
            Step.Event("session.execution.succeeded", new { })
        ];

    /// <summary>
    /// Sends an event to everyone who listens to the stream, now.
    /// </summary>
    /// <param name="type">The type of the event.</param>
    /// <param name="data">The data of the event; the session is added to it.</param>
    /// <returns>A task that completes when it has been sent.</returns>
    public async Task Emit(string type, object data)
    {
        JsonObject body = JsonSerializer.SerializeToNode(data)!.AsObject();
        body["sessionID"] = SessionId;
        string line = $"data: {new JsonObject { ["id"] = $"evt_{Interlocked.Increment(ref events)}", ["type"] = type, ["data"] = body }.ToJsonString()}\n\n";
        StreamWriter[] writers;

        lock (gate)
        {
            writers = [.. streams];
        }

        foreach (StreamWriter writer in writers)
        {
            try
            {
                await writer.WriteAsync(line);
                await writer.FlushAsync();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or HttpListenerException)
            {
                lock (gate)
                {
                    streams.Remove(writer);
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Close();

        try
        {
            await serving;
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    private async Task Serve()
    {
        while (!stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        string path = context.Request.Url!.AbsolutePath;
        string method = context.Request.HttpMethod;
        string text = context.Request.HasEntityBody ? await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync() : string.Empty;
        JsonElement body = JsonDocument.Parse(text.Length > 0 ? text : "{}").RootElement.Clone();

        lock (gate)
        {
            recorded.Add(new Recorded(method, path, body, context.Request.Headers["Authorization"], context.Request.Headers["x-opencode-directory"]));
        }

        try
        {
            if (method == "GET" && path == "/api/event")
            {
                await Listen(context);
                return;
            }

            (int status, object? data) = await Answer(method, path, body);
            context.Response.StatusCode = status;

            if (data is not null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
            }

            context.Response.Close();
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task Listen(HttpListenerContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        StreamWriter writer = new(context.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = false };

        lock (gate)
        {
            streams.Add(writer);
        }

        await writer.WriteAsync("data: {\"id\":\"evt_0\",\"type\":\"server.connected\",\"data\":{}}\n\n");
        await writer.FlushAsync();

        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<(int Status, object? Data)> Answer(string method, string path, JsonElement body)
    {
        switch (method, path)
        {
            case ("GET", "/api/mcp"):
                int poll = Interlocked.Increment(ref polls);
                return (200, new { data = McpStatuses.Count == 0 ? Array.Empty<object>() : (object[])[new { name = "liveai", status = new { status = McpStatuses[Math.Min(poll, McpStatuses.Count) - 1] } }, .. OtherMcpServers.Select(name => new { name, status = new { status = "connected" } })] });
            case ("POST", var connect) when connect.StartsWith("/api/experimental/mcp/", StringComparison.Ordinal) && connect.EndsWith("/connect", StringComparison.Ordinal):
                if (ConnectRecovers)
                {
                    McpStatuses = ["connected"];
                }

                return (204, null);
            case ("GET", "/api/model"):
                return (200, new
                {
                    data = new object[]
                    {
                        new { providerID = "fake", modelID = "model", id = "model", limit = new { context = 32000, output = 4000 }, variants = new[] { new { id = "low" }, new { id = "medium" }, new { id = "high" } } },
                        new { providerID = "fake", modelID = "big", id = "big", limit = new { context = 64000, output = 8000 }, variants = new[] { new { id = "low" }, new { id = "high" }, new { id = "max" } } }
                    }
                });
            case ("GET", "/api/model/default"):
                return (200, new { data = new { providerID = "fake", modelID = "model", id = "model" } });
            case ("POST", "/api/session"):
                Interlocked.Increment(ref created);
                return (200, new { data = new { id = SessionId } });
            case ("GET", var existing) when existing.StartsWith("/api/session/", StringComparison.Ordinal) && existing.Split('/').Length == 4:
                string id = existing.Split('/')[3];
                return KnownSessions.Contains(id) || (created > 0 && id == SessionId) ? (200, new { data = new { id } }) : (404, new { _tag = "SessionNotFoundError", message = $"Session not found: {id}" });
            case ("POST", var model) when model.EndsWith("/model", StringComparison.Ordinal):
                return (204, null);
            case ("POST", var cancel) when cancel.EndsWith("/interrupt", StringComparison.Ordinal):
                bool was = running;
                interrupt.TrySetResult();
                return (200, new { interrupted = was });
            case ("POST", var compact) when compact.EndsWith("/compact", StringComparison.Ordinal):
                _ = Task.Run(() => Play(OnPrompt(body)));
                return (200, new { data = new { id = "msg_compact" } });
            case ("POST", var prompt) when prompt.EndsWith("/prompt", StringComparison.Ordinal):
                if (running && body.TryGetProperty("delivery", out JsonElement delivery) && delivery.GetString() == "steer")
                {
                    steer.TrySetResult();
                }
                else
                {
                    _ = Task.Run(() => Play(OnPrompt(body)));
                }

                return (200, new { data = new { id = "msg_prompt" } });
            default:
                await Task.CompletedTask;
                return (404, new { message = $"Not mocked: {method} {path}" });
        }
    }

    private async Task Play(IReadOnlyList<Step> steps)
    {
        running = true;
        interrupt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        steer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            foreach (Step step in steps)
            {
                if (step.Pause > TimeSpan.Zero)
                {
                    await Task.Delay(step.Pause);
                }

                if (step.UntilInterrupted)
                {
                    await interrupt.Task;
                    await Emit("session.execution.interrupted", new { reason = "user" });
                    return;
                }

                if (step.UntilSteered)
                {
                    await steer.Task;
                }

                if (step.Type is not null)
                {
                    await Emit(step.Type, step.Data ?? new { });
                }
            }
        }
        finally
        {
            running = false;
        }
    }

    /// <summary>
    /// A request the server was sent.
    /// </summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The path.</param>
    /// <param name="Body">The JSON body, or an empty object.</param>
    /// <param name="Authorization">The authorization header.</param>
    /// <param name="Directory">The directory header.</param>
    public sealed record Recorded(string Method, string Path, JsonElement Body, string? Authorization, string? Directory);

    /// <summary>
    /// One thing the server does while it plays a prompt.
    /// </summary>
    public sealed record Step
    {
        /// <summary>
        /// Gets the type of the event to send, or <see langword="null" /> for none.
        /// </summary>
        public string? Type { get; init; }

        /// <summary>
        /// Gets the data of the event.
        /// </summary>
        public object? Data { get; init; }

        /// <summary>
        /// Gets how long to wait before the step.
        /// </summary>
        public TimeSpan Pause { get; init; }

        /// <summary>
        /// Gets a value indicating whether the step waits for an interrupt and then ends the prompt as interrupted.
        /// </summary>
        public bool UntilInterrupted { get; init; }

        /// <summary>
        /// Gets a value indicating whether the step waits for input that is steered into the prompt.
        /// </summary>
        public bool UntilSteered { get; init; }

        /// <summary>
        /// Makes a step that sends an event.
        /// </summary>
        /// <param name="type">The type of the event.</param>
        /// <param name="data">Its data.</param>
        /// <returns>The step.</returns>
        public static Step Event(string type, object data)
            => new() { Type = type, Data = data };

        /// <summary>
        /// Makes a step that waits for an interrupt.
        /// </summary>
        /// <returns>The step.</returns>
        public static Step Interrupted()
            => new() { UntilInterrupted = true };

        /// <summary>
        /// Makes a step that waits for steered input before it sends an event.
        /// </summary>
        /// <param name="type">The type of the event.</param>
        /// <param name="data">Its data.</param>
        /// <returns>The step.</returns>
        public static Step Steered(string type, object data)
            => new() { Type = type, Data = data, UntilSteered = true };
    }
}
