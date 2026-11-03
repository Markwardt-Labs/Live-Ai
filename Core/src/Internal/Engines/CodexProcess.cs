namespace Markwardt.LiveAi;

/// <summary>
/// A <c>codex app-server</c> process, which holds the conversation as a thread and is spoken to in JSON-RPC lines. Every prompt is a turn of the thread, and the model, the effort and the schema of the answer are given with each one, so they change without a new process.
/// </summary>
/// <param name="engine">Reads the items the process reports.</param>
/// <param name="process">The started process, whose streams are redirected.</param>
/// <param name="start">What the process was started for: its engine session is the thread to resume, and its instructions are what the AI is told about its tools together with the instructions of the user, which the thread is started with.</param>
internal sealed class CodexProcess(CodexEngine engine, Process process, PromptEngineStart start) : IPromptProcess
{
    private readonly Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

    private readonly TimeSpan grace = TimeSpan.FromSeconds(3);

    private readonly TimeSpan answer = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim writing = new(1, 1);

    private readonly Queue<JsonElement> backlog = new();

    private string? thread = start.AiSessionId;

    private volatile string? turn;

    private bool initialized;

    private bool opened;

    private PromptContext? context;

    private int requests;

    private int disposed;

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptEngineEvent> Run(PromptInput prompt, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        bool completed = false;

        try
        {
            await Open(cancellation);

            foreach (string note in prompt.Notes)
            {
                await Request("thread/inject_items", new JsonObject
                {
                    ["threadId"] = thread,
                    ["items"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "developer", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = note }) })
                }, cancellation);
            }

            JsonObject parameters = new()
            {
                ["threadId"] = thread,
                ["input"] = Input(prompt),
                ["model"] = prompt.Model
            };

            if (prompt.Settings.TryGetValue("effort", out string? effort))
            {
                parameters["effort"] = effort;
            }

            if (prompt.Schema is not null)
            {
                parameters["outputSchema"] = JsonNode.Parse(prompt.Schema);
            }

            JsonElement started = await Request("turn/start", parameters, cancellation);
            turn = started.TryGetProperty("turn", out JsonElement begun) && begun.TryGetProperty("id", out JsonElement id) ? id.GetString() : null;

            yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = thread! };

            PromptUsage used = new();

            while (!completed)
            {
                JsonElement message = await Next(cancellation) ?? throw await Ended();

                if (!Notification(message, out string method, out JsonElement values))
                {
                    continue;
                }

                if (method == "thread/tokenUsage/updated" && Matches(values, "turnId"))
                {
                    used = used.Plus(Spent(values));
                    context = Filled(values) ?? context;
                }
                else if (method == "turn/completed" && values.TryGetProperty("turn", out JsonElement finished) && finished.TryGetProperty("id", out JsonElement finishedId) && finishedId.GetString() == turn)
                {
                    completed = true;
                    string status = finished.TryGetProperty("status", out JsonElement state) ? state.GetString() ?? string.Empty : string.Empty;

                    if (status == "failed")
                    {
                        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = engine.Failure(finished) };
                    }
                    else
                    {
                        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Event, Event = new PromptUsageEvent { Usage = used } };
                        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished };
                    }
                }
                else
                {
                    foreach (PromptEngineEvent output in engine.Read(method, values, turn))
                    {
                        yield return output;
                    }
                }
            }
        }
        finally
        {
            turn = null;

            if (!completed)
            {
                Stop();
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> Steer(PromptInput input, CancellationToken cancellation = default)
    {
        string? active = turn;

        if (active is null)
        {
            return false;
        }

        foreach (string note in input.Notes)
        {
            await Write(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref requests),
                ["method"] = "thread/inject_items",
                ["params"] = new JsonObject
                {
                    ["threadId"] = thread,
                    ["items"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "developer", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = note }) })
                }
            }.ToJsonString(), cancellation);
        }

        await Write(new JsonObject
        {
            ["id"] = Interlocked.Increment(ref requests),
            ["method"] = "turn/steer",
            ["params"] = new JsonObject { ["threadId"] = thread, ["expectedTurnId"] = active, ["input"] = Input(input) }
        }.ToJsonString(), cancellation);

        return true;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptEngineEvent> Compact([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        bool completed = false;

        try
        {
            await Open(cancellation);
            await Request("thread/compact/start", new JsonObject { ["threadId"] = thread }, cancellation);

            while (!completed)
            {
                JsonElement message = await Next(cancellation) ?? throw await Ended();

                if (Notification(message, out string method, out JsonElement values) && method == "turn/completed")
                {
                    completed = true;

                    if (values.TryGetProperty("turn", out JsonElement finished) && finished.TryGetProperty("status", out JsonElement state) && state.GetString() == "failed")
                    {
                        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = engine.Failure(finished) };
                    }
                    else
                    {
                        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = thread! };
                        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished };
                    }
                }
                else if (method == "thread/tokenUsage/updated")
                {
                    context = Filled(values) ?? context;
                }
            }
        }
        finally
        {
            if (!completed)
            {
                Stop();
            }
        }
    }

    /// <inheritdoc />
    public async Task<PromptContext> GetContext(CancellationToken cancellation = default)
    {
        await Open(cancellation);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(engine.ContextWait);

        try
        {
            while (context is null)
            {
                JsonElement message = await Next(limit.Token) ?? throw await Ended();

                if (Notification(message, out string method, out JsonElement values) && method == "thread/tokenUsage/updated")
                {
                    context = Filled(values);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
        }

        return context ?? new PromptContext { Tokens = 0, Limit = 0 };
    }

    /// <inheritdoc />
    public Task Interrupt(CancellationToken cancellation = default)
        => turn is { } running && thread is { } id
            ? Write(new JsonObject { ["id"] = Interlocked.Increment(ref requests), ["method"] = "turn/interrupt", ["params"] = new JsonObject { ["threadId"] = id, ["turnId"] = running } }.ToJsonString(), cancellation)
            : Task.CompletedTask;

    /// <inheritdoc />
    public void Stop()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        using CancellationTokenSource timeout = new(grace);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Stop();
        }

        process.Dispose();
    }

    private async Task Open(CancellationToken cancellation = default)
    {
        if (!initialized)
        {
            await Request("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "Markwardt.LiveAi", ["version"] = "1" } }, cancellation);
            await Write(new JsonObject { ["method"] = "initialized" }.ToJsonString(), cancellation);
            initialized = true;
        }

        if (opened)
        {
            return;
        }

        JsonObject parameters = new()
        {
            ["cwd"] = start.WorkingFolder,
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["developerInstructions"] = start.Instructions
        };

        if (thread is null)
        {
            parameters["ephemeral"] = false;
        }
        else
        {
            parameters["threadId"] = thread;
        }

        JsonElement result = await Request(thread is null ? "thread/start" : "thread/resume", parameters, cancellation);
        thread = result.TryGetProperty("thread", out JsonElement found) && found.TryGetProperty("id", out JsonElement id) && id.GetString() is { Length: > 0 } text ? text : throw new PromptSessionException("Codex did not report the thread of the conversation.");
        opened = true;
    }

    private JsonArray Input(PromptInput prompt)
    {
        JsonArray input = new(new JsonObject { ["type"] = "text", ["text"] = prompt.Text, ["text_elements"] = new JsonArray() });

        foreach (string file in prompt.Files)
        {
            input.Add(new JsonObject { ["type"] = "localImage", ["path"] = file });
        }

        return input;
    }

    private PromptUsage Spent(JsonElement values)
        => values.TryGetProperty("tokenUsage", out JsonElement usage) && usage.TryGetProperty("last", out JsonElement last) && last.ValueKind == JsonValueKind.Object
            ? new PromptUsage { InputTokens = Count(last, "inputTokens") - Count(last, "cachedInputTokens"), CachedInputTokens = Count(last, "cachedInputTokens"), OutputTokens = Count(last, "outputTokens") }
            : new PromptUsage();

    private PromptContext? Filled(JsonElement values)
        => values.TryGetProperty("tokenUsage", out JsonElement usage) && usage.TryGetProperty("last", out JsonElement last) && last.ValueKind == JsonValueKind.Object
            ? new PromptContext { Tokens = Count(last, "totalTokens"), Limit = Count(usage, "modelContextWindow") }
            : null;

    private long Count(JsonElement values, string name)
        => values.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) ? count : 0;

    private bool Matches(JsonElement values, string name)
        => values.TryGetProperty(name, out JsonElement value) && value.GetString() == turn;

    private bool Notification(JsonElement message, out string method, out JsonElement values)
    {
        method = message.TryGetProperty("method", out JsonElement found) && found.ValueKind == JsonValueKind.String ? found.GetString() ?? string.Empty : string.Empty;
        values = message.TryGetProperty("params", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object ? parameters : default;
        return method.Length > 0 && values.ValueKind == JsonValueKind.Object;
    }

    private async Task<JsonElement?> Next(CancellationToken cancellation = default)
    {
        while (true)
        {
            JsonElement? message = backlog.Count > 0 ? backlog.Dequeue() : await Read(cancellation);

            if (message is not { } found)
            {
                return null;
            }

            if (found.TryGetProperty("method", out _))
            {
                if (found.TryGetProperty("id", out JsonElement id))
                {
                    await Write(new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "The library does not answer requests of the tool." } }.ToJsonString(), cancellation);
                    continue;
                }

                return found;
            }
        }
    }

    private async Task<JsonElement?> Read(CancellationToken cancellation = default)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);

                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private async Task<JsonElement> Request(string method, JsonObject parameters, CancellationToken cancellation = default)
    {
        int id = Interlocked.Increment(ref requests);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(answer);

        try
        {
            await Write(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString(), limit.Token);

            while (await Read(limit.Token) is { } message)
            {
                if (message.TryGetProperty("method", out _))
                {
                    if (message.TryGetProperty("id", out JsonElement asked))
                    {
                        await Write(new JsonObject { ["id"] = JsonNode.Parse(asked.GetRawText()), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "The library does not answer requests of the tool." } }.ToJsonString(), limit.Token);
                    }
                    else
                    {
                        backlog.Enqueue(message);
                    }

                    continue;
                }

                if (message.TryGetProperty("id", out JsonElement found) && found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out int number) && number == id)
                {
                    if (message.TryGetProperty("error", out JsonElement error))
                    {
                        throw new PromptSessionException(error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement text) && text.GetString() is { Length: > 0 } reason ? reason : $"{engine.Identifier} refused a request.");
                    }

                    return message.TryGetProperty("result", out JsonElement result) ? result.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Stop();
            throw new PromptSessionException($"{engine.Identifier} did not answer a request in time.");
        }

        throw await Ended();
    }

    private async Task<PromptSessionException> Ended()
    {
        string detail = (await errors).Trim();
        return new PromptSessionException(detail.Length > 0 ? detail : $"{engine.Identifier} stopped.");
    }

    private async Task Write(string line, CancellationToken cancellation = default)
    {
        await writing.WaitAsync(cancellation);

        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellation);
            await process.StandardInput.FlushAsync(cancellation);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            writing.Release();
        }
    }
}
