namespace Markwardt.LiveAi;

/// <summary>
/// A running local MCP server that offers a set of <see cref="PromptTool" />s to the AI's tool.
/// </summary>
internal interface IPromptToolServer : IAsyncDisposable
{
    /// <summary>
    /// Gets how the tool reaches the server.
    /// </summary>
    ToolEndpoint Endpoint { get; }
}

/// <inheritdoc />
/// <param name="tools">The tools to offer.</param>
/// <param name="listener">The started listener to serve on.</param>
/// <param name="endpoint">How the tool reaches the listener.</param>
internal sealed class PromptToolServer(IReadOnlyList<PromptTool> tools, HttpListener listener, ToolEndpoint endpoint) : IPromptToolServer
{
    private readonly CancellationTokenSource stopping = new();

    private readonly JsonElement noArguments = JsonDocument.Parse("{}").RootElement;

    private Task serving = Task.CompletedTask;

    private int disposed;

    /// <inheritdoc />
    public ToolEndpoint Endpoint { get; } = endpoint;

    /// <summary>
    /// Starts answering requests.
    /// </summary>
    public void Start()
        => serving = Task.Run(Serve, CancellationToken.None);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        await stopping.CancelAsync();
        listener.Close();
        await serving;
        stopping.Dispose();
    }

    private async Task Serve()
    {
        List<Task> handling = [];

        while (!stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            handling.RemoveAll(task => task.IsCompleted);
            handling.Add(Handle(context));
        }

        await Task.WhenAll(handling);
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            if (!Authorized(context.Request))
            {
                Reply(context, 401);
            }
            else if (context.Request.HttpMethod != "POST")
            {
                context.Response.AddHeader("Allow", "POST");
                Reply(context, 405);
            }
            else
            {
                await Answer(context);
            }
        }
        catch (Exception)
        {
            context.Response.Abort();
        }
    }

    private async Task Answer(HttpListenerContext context)
    {
        JsonDocument document;

        try
        {
            document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: stopping.Token);
        }
        catch (JsonException)
        {
            await Respond(context, Failure(null, -32700, "Parse error"));
            return;
        }

        using (document)
        {
            JsonElement request = document.RootElement;

            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String || method.GetString() is not { } name)
            {
                await Respond(context, Failure(null, -32600, "Invalid request"));
            }
            else if (!request.TryGetProperty("id", out JsonElement id))
            {
                Reply(context, 202);
            }
            else
            {
                JsonElement parameters = request.TryGetProperty("params", out JsonElement found) ? found : noArguments;
                JsonNode? identifier = JsonNode.Parse(id.GetRawText());
                await Respond(context, await Dispatch(name, parameters, identifier));
            }
        }
    }

    private async Task<JsonObject> Dispatch(string method, JsonElement parameters, JsonNode? id)
    {
        switch (method)
        {
            case "initialize":
                string version = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out JsonElement requested) && requested.GetString() is { Length: > 0 } text ? text : "2025-06-18";
                return Success(id, new JsonObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "Markwardt.LiveAi", ["version"] = "1" }
                });
            case "ping":
                return Success(id, new JsonObject());
            case "tools/list":
                return Success(id, new JsonObject { ["tools"] = new JsonArray(tools.Select(Describe).ToArray()) });
            case "tools/call":
                return await Call(parameters, id);
            default:
                return Failure(id, -32601, $"Method not found: {method}");
        }
    }

    private async Task<JsonObject> Call(JsonElement parameters, JsonNode? id)
    {
        string? name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out JsonElement found) ? found.GetString() : null;

        if (tools.FirstOrDefault(candidate => candidate.Name == name) is not { } tool)
        {
            return Failure(id, -32602, $"Unknown tool: {name}");
        }

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement given) && given.ValueKind == JsonValueKind.Object ? given : noArguments;

        string callId = parameters.TryGetProperty("_meta", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("claudecode/toolUseId", out JsonElement toolUse) && toolUse.ValueKind == JsonValueKind.String ? toolUse.GetString()! : id?.ToString() ?? string.Empty;
        string output;
        bool failed;

        try
        {
            output = await tool.Handler(arguments, stopping.Token);
            failed = false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            output = exception.Message;
            failed = true;
        }

        return Success(id, Content(output, failed));
    }

    private JsonNode Describe(PromptTool tool)
        => new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText())
        };

    private JsonObject Content(string text, bool isError)
        => new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };

    private JsonObject Success(JsonNode? id, JsonObject result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private JsonObject Failure(JsonNode? id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private bool Authorized(HttpListenerRequest request)
        => request.Headers["Authorization"] is { } header && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes($"Bearer {Endpoint.Token}"));

    private void Reply(HttpListenerContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.Close();
    }

    private async Task Respond(HttpListenerContext context, JsonObject body)
    {
        ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, stopping.Token);
        context.Response.Close();
    }
}
