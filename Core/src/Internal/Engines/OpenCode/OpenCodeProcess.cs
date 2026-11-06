namespace Markwardt.LiveAi;

/// <summary>
/// A running <c>opencode serve --stdio</c>: the HTTP server of OpenCode 2.x that holds one conversation of the session, talked to with requests and read through its stream of server-sent events. The server lives as long as the standard input of the process is open.
/// </summary>
/// <param name="engine">The engine that started the process.</param>
/// <param name="process">The started process.</param>
/// <param name="start">What the process was started for.</param>
internal sealed class OpenCodeProcess(OpenCodeEngine engine, Process process, PromptEngineStart start) : IPromptProcess
{
    private readonly Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

    private readonly TimeSpan grace = TimeSpan.FromSeconds(3);

    private readonly TimeSpan answer = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource stopping = new();

    private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });

    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Dictionary<string, string> names = [];

    private readonly List<Choice> choices = [];

    private HttpClient? http;

    private string? session = start.AiSessionId;

    private string? provider;

    private string? model;

    private string? variant;

    private bool opened;

    private volatile bool running;

    private PromptContext? context;

    private PromptUsage spent = new();

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
            await Configure(prompt, cancellation);

            yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = session! };

            Drain();
            running = true;
            spent = new PromptUsage();
            await Send(HttpMethod.Post, $"/api/session/{session}/prompt", Input(Said(prompt), prompt.Attachments, null), cancellation);

            await foreach (PromptEngineEvent output in Consume(prompt.Schema is not null, false, cancellation))
            {
                yield return output;
            }

            completed = true;
        }
        finally
        {
            running = false;

            if (!completed)
            {
                Stop();
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> Steer(PromptInput input, CancellationToken cancellation = default)
    {
        if (!running || session is null)
        {
            return false;
        }

        await Send(HttpMethod.Post, $"/api/session/{session}/prompt", Input(Said(input), input.Attachments, "steer"), cancellation);
        return true;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptEngineEvent> Compact([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        bool completed = false;

        try
        {
            await Open(cancellation);

            yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = session! };

            Drain();
            running = true;
            await Send(HttpMethod.Post, $"/api/session/{session}/compact", new JsonObject(), cancellation);

            await foreach (PromptEngineEvent output in Consume(false, true, cancellation))
            {
                if (output.Kind is PromptEngineEventKind.Failed or PromptEngineEventKind.Finished)
                {
                    yield return output;
                }
            }

            completed = true;
        }
        finally
        {
            running = false;

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
        await Models(cancellation);

        if (context is { } known)
        {
            return known;
        }

        long declared = start.Settings.TryGetValue("context", out string? size) && long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out long tokens) ? tokens : 0;
        long limit = Limit();
        PromptContext read = await engine.ReadContext(session!, limit > 0 ? limit : declared, cancellation);
        return context = read;
    }

    /// <inheritdoc />
    public async Task Interrupt(CancellationToken cancellation = default)
    {
        if (running && session is not null)
        {
            await Send(HttpMethod.Post, $"/api/session/{session}/interrupt", null, cancellation);
        }
    }

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

        await stopping.CancelAsync();

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

        http?.Dispose();
        process.Dispose();
    }

    private async IAsyncEnumerable<PromptEngineEvent> Consume(bool structured, bool compacting, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        while (true)
        {
            JsonElement message = await Next(cancellation) ?? throw await Ended();

            if (!message.TryGetProperty("type", out JsonElement kind) || !message.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("sessionID", out JsonElement owner) || owner.GetString() != session)
            {
                continue;
            }

            switch (kind.GetString())
            {
                case "session.text.delta" when data.TryGetProperty("delta", out JsonElement delta) && delta.GetString() is { Length: > 0 } piece:
                    yield return Event(new PromptDeltaEvent { Text = piece });
                    break;
                case "session.text.ended" when data.TryGetProperty("text", out JsonElement text) && text.GetString()?.Trim() is { Length: > 0 } reply:
                    yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = structured ? Unfenced(reply) : reply };
                    break;
                case "session.tool.input.started" when data.TryGetProperty("id", out JsonElement id) && data.TryGetProperty("name", out JsonElement name):
                    names[id.GetString() ?? string.Empty] = name.GetString() ?? string.Empty;
                    break;
                case "session.tool.called":
                    yield return Event(Called(data));
                    break;
                case "session.tool.success":
                    yield return Event(new PromptToolResultEvent { Id = Id(data), Output = Output(data), IsError = false });
                    break;
                case "session.tool.failed":
                    yield return Event(new PromptToolResultEvent { Id = Id(data), Output = engine.ErrorText(data), IsError = true });
                    break;
                case "session.step.ended":
                    Counted(data);
                    break;
                case "session.compaction.failed":
                    yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = engine.ErrorText(data) };
                    yield break;
                case "session.retry.scheduled":
                    await Abandon(cancellation);
                    yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = engine.ErrorText(data) };
                    yield break;
                case "session.execution.failed":
                    yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = engine.ErrorText(data) };
                    yield break;
                case "session.execution.succeeded" or "session.execution.interrupted":
                    if (!compacting)
                    {
                        yield return Event(new PromptUsageEvent { Usage = spent });
                    }

                    yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished };
                    yield break;
            }
        }
    }

    private PromptToolCallEvent Called(JsonElement data)
    {
        string id = Id(data);
        string title = names.GetValueOrDefault(id, string.Empty);
        (string? server, string tool) = Split(title);
        return new PromptToolCallEvent { Id = id, Server = server, Name = tool, Input = data.TryGetProperty("input", out JsonElement input) ? JsonNode.Parse(input.GetRawText())?.ToJsonString() ?? "{}" : "{}" };
    }

    private string Id(JsonElement data)
        => data.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty;

    private string Output(JsonElement data)
        => data.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array ? string.Join("\n", content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString())) : string.Empty;

    private (string? Server, string Name) Split(string title)
    {
        foreach (PromptEngineServer server in start.Servers)
        {
            string prefix = $"{engine.ToolPrefix(server.Name)}_";

            if (title.StartsWith(prefix, StringComparison.Ordinal) && title.Length > prefix.Length)
            {
                return (server.Name, title[prefix.Length..]);
            }
        }

        return (null, title);
    }

    private void Counted(JsonElement data)
    {
        if (!data.TryGetProperty("tokens", out JsonElement tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        long cached = tokens.TryGetProperty("cache", out JsonElement cache) ? engine.Count(cache, "read") : 0;
        long input = engine.Count(tokens, "input");
        long output = engine.Count(tokens, "output") + engine.Count(tokens, "reasoning");
        decimal cost = data.TryGetProperty("cost", out JsonElement amount) && amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out decimal paid) ? paid : 0;
        spent = spent.Plus(new PromptUsage { InputTokens = input, CachedInputTokens = cached, OutputTokens = output, CostUsd = cost > 0 ? cost : null });
        context = new PromptContext { Tokens = input + cached + output, Limit = Limit() };
    }

    private long Limit()
        => choices.FirstOrDefault(choice => choice.Provider == provider && choice.Model == model)?.Context ?? 0;

    private PromptEngineEvent Event(PromptEvent output)
        => new() { Kind = PromptEngineEventKind.Event, Event = output };

    private string Unfenced(string reply)
    {
        if (!reply.StartsWith("```", StringComparison.Ordinal))
        {
            return reply;
        }

        int open = reply.IndexOf('\n');
        int close = reply.LastIndexOf("```", StringComparison.Ordinal);
        return open >= 0 && close > open ? reply[(open + 1)..close].Trim() : reply;
    }

    private string Said(PromptInput prompt)
    {
        IPromptEngine generic = engine;
        string said = generic.Plain(prompt);
        return prompt.Schema is { } schema ? $"{said}\n\nAnswer with a single JSON object that follows this JSON Schema and nothing else: no code fence and no commentary.\n{schema}" : said;
    }

    private JsonObject Input(string said, IReadOnlyList<PromptAttachment> attachments, string? delivery)
    {
        JsonObject body = new() { ["text"] = said };

        if (delivery is not null)
        {
            body["delivery"] = delivery;
        }

        if (attachments.Count > 0)
        {
            body["files"] = new JsonArray([.. attachments.Select(attachment => (JsonNode)new JsonObject { ["uri"] = $"data:{attachment.MediaType};base64,{Convert.ToBase64String(attachment.Content.Span)}", ["name"] = attachment.Name })]);
        }

        return body;
    }

    private async Task Abandon(CancellationToken cancellation = default)
    {
        try
        {
            await Send(HttpMethod.Post, $"/api/session/{session}/interrupt", null, cancellation);
        }
        catch (PromptSessionException)
        {
        }
    }

    private void Drain()
    {
        while (events.Reader.TryRead(out _))
        {
        }
    }

    private async Task Open(CancellationToken cancellation = default)
    {
        if (opened)
        {
            return;
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(answer);
        string url;

        try
        {
            string line = await process.StandardOutput.ReadLineAsync(limit.Token) ?? throw await Ended();
            using JsonDocument document = JsonDocument.Parse(line);
            url = document.RootElement.GetProperty("url").GetString() ?? throw await Ended();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw await Ended();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Stop();
            throw new PromptSessionException($"{engine.Identifier} did not start its server in time.");
        }

        string password = process.StartInfo.Environment.TryGetValue("OPENCODE_SERVER_PASSWORD", out string? given) ? given ?? string.Empty : string.Empty;
        http = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}")));
        http.DefaultRequestHeaders.Add("x-opencode-directory", start.WorkingFolder);

        _ = Task.Run(() => Pump(stopping.Token), CancellationToken.None);
        await connected.Task.WaitAsync(answer, cancellation);
        await Ready(cancellation);

        if (session is null)
        {
            JsonElement created = await Send(HttpMethod.Post, "/api/session", new JsonObject(), cancellation);
            session = created.TryGetProperty("id", out JsonElement id) && id.GetString() is { Length: > 0 } text ? text : throw new PromptSessionException($"{engine.Identifier} did not report the session.");
        }
        else
        {
            _ = await Send(HttpMethod.Get, $"/api/session/{session}", null, cancellation);
        }

        opened = true;
    }

    private async Task Ready(CancellationToken cancellation = default)
    {
        string[] wanted = [.. start.Servers.Select(server => server.Name)];
        string[] required = [.. start.Servers.Where(server => server.Tools is not null).Select(server => server.Name)];
        DateTime until = DateTime.UtcNow + engine.ConnectWait;
        int attempts = 0;

        while (true)
        {
            JsonElement list = await Send(HttpMethod.Get, "/api/mcp", null, cancellation);
            IReadOnlyList<(string Name, string Status)> servers = [.. list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(server => (server.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty, server.TryGetProperty("status", out JsonElement status) && status.TryGetProperty("status", out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty)) : []];
            bool settled = wanted.All(name => servers.Any(server => server.Name == name && server.Status is not ("connecting" or "pending" or "")));
            string[] broken = [.. servers.Where(server => wanted.Contains(server.Name) && server.Status is "failed" or "needs_auth").Select(server => server.Name)];

            if (broken.Length > 0 && attempts < 2 && DateTime.UtcNow < until)
            {
                attempts++;

                foreach (string name in broken)
                {
                    try
                    {
                        _ = await Send(HttpMethod.Post, $"/api/experimental/mcp/{Uri.EscapeDataString(name)}/connect", null, cancellation);
                    }
                    catch (PromptSessionException)
                    {
                    }
                }

                await Task.Delay(engine.PollInterval, cancellation);
                continue;
            }

            if (required.FirstOrDefault(name => broken.Contains(name)) is { } lost)
            {
                throw new PromptSessionException($"{engine.Identifier} could not connect to the tools of the library (the MCP server '{lost}'), so it would have none.");
            }

            if (settled)
            {
                return;
            }

            if (DateTime.UtcNow >= until)
            {
                if (required.All(name => servers.Any(server => server.Name == name && server.Status == "connected")))
                {
                    return;
                }

                throw new PromptSessionException($"{engine.Identifier} did not connect to its tools in time.");
            }

            await Task.Delay(engine.PollInterval, cancellation);
        }
    }

    private async Task Configure(PromptInput prompt, CancellationToken cancellation = default)
    {
        await Models(cancellation);

        int slash = prompt.Model.IndexOf('/');

        if (slash <= 0)
        {
            return;
        }

        (string providerId, string modelId) = (prompt.Model[..slash], prompt.Model[(slash + 1)..]);
        string? wantedVariant = prompt.Settings.GetValueOrDefault("variant");

        if (providerId == provider && modelId == model && wantedVariant == variant)
        {
            return;
        }

        JsonObject choice = new() { ["id"] = modelId, ["providerID"] = providerId };

        if (wantedVariant is not null)
        {
            choice["variant"] = wantedVariant;
        }

        _ = await Send(HttpMethod.Post, $"/api/session/{session}/model", new JsonObject { ["model"] = choice }, cancellation);
        (provider, model, variant) = (providerId, modelId, wantedVariant);
    }

    private async Task Models(CancellationToken cancellation = default)
    {
        if (choices.Count > 0)
        {
            return;
        }

        JsonElement list = await Send(HttpMethod.Get, "/api/model", null, cancellation);

        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in list.EnumerateArray())
            {
                string providerId = entry.TryGetProperty("providerID", out JsonElement owner) ? owner.GetString() ?? string.Empty : string.Empty;
                string modelId = entry.TryGetProperty("modelID", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty;
                long window = entry.TryGetProperty("limit", out JsonElement limit) ? engine.Count(limit, "context") : 0;
                IReadOnlyList<string> variants = entry.TryGetProperty("variants", out JsonElement options) && options.ValueKind == JsonValueKind.Array ? [.. options.EnumerateArray().Select(option => option.TryGetProperty("id", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty).Where(name => name.Length > 0)] : [];
                choices.Add(new Choice(providerId, modelId, window, variants));
            }
        }

        if (provider is null && (await TryDefault(cancellation)) is { } fallback)
        {
            (provider, model) = fallback;
        }
    }

    private async Task<(string Provider, string Model)?> TryDefault(CancellationToken cancellation = default)
    {
        try
        {
            JsonElement value = await Send(HttpMethod.Get, "/api/model/default", null, cancellation);
            string? providerId = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("providerID", out JsonElement owner) ? owner.GetString() : null;
            string? modelId = value.ValueKind == JsonValueKind.Object && (value.TryGetProperty("modelID", out JsonElement id) || value.TryGetProperty("id", out id)) ? id.GetString() : null;
            return providerId is not null && modelId is not null ? (providerId, modelId) : null;
        }
        catch (PromptSessionException)
        {
            return null;
        }
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, JsonObject? body, CancellationToken cancellation = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(answer);
        using HttpRequestMessage request = new(method, path) { Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        try
        {
            using HttpResponseMessage response = await http!.SendAsync(request, limit.Token);
            string text = await response.Content.ReadAsStringAsync(limit.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new PromptSessionException(Reason(text, response));
            }

            if (text.Length == 0)
            {
                return JsonDocument.Parse("{}").RootElement.Clone();
            }

            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out JsonElement data) ? data.Clone() : document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Stop();
            throw new PromptSessionException($"{engine.Identifier} did not answer a request in time.");
        }
        catch (HttpRequestException)
        {
            throw await Ended();
        }
    }

    private string Reason(string text, HttpResponseMessage response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return engine.ErrorText(document.RootElement);
        }
        catch (JsonException)
        {
            return text.Length > 0 ? text : $"{engine.Identifier} answered {(int)response.StatusCode}.";
        }
    }

    private async Task Pump(CancellationToken cancellation)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "/api/event");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using HttpResponseMessage response = await http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
            using StreamReader reader = new(stream, Encoding.UTF8);

            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line[5..]);

                    if (document.RootElement.TryGetProperty("type", out JsonElement kind) && kind.GetString() == "server.connected")
                    {
                        connected.TrySetResult();
                    }

                    events.Writer.TryWrite(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            events.Writer.TryComplete();
            connected.TrySetResult();
        }
    }

    private async Task<JsonElement?> Next(CancellationToken cancellation = default)
    {
        try
        {
            return await events.Reader.ReadAsync(cancellation);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private async Task<PromptSessionException> Ended()
    {
        string detail = process.HasExited ? (await errors).Trim() : string.Empty;
        return new PromptSessionException(detail.Length > 0 ? detail : $"{engine.Identifier} stopped.");
    }

    private sealed record Choice(string Provider, string Model, long Context, IReadOnlyList<string> Variants);
}
