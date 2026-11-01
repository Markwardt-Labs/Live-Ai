namespace Markwardt.LiveAi;

/// <summary>
/// Codex, driven through <c>codex app-server</c>, which holds the conversation as a thread that every prompt is a turn of.
/// </summary>
/// <param name="command">The command that starts Codex.</param>
/// <param name="configFolder">The folder Codex keeps its state in, or <see langword="null" /> for <c>CODEX_HOME</c> or <c>.codex</c> in the user's home folder. It is also passed to the tool as <c>CODEX_HOME</c>, so the library and the tool look in the same place.</param>
/// <param name="models">The model of each capability (the names a <see cref="CodexModelCatalog" /> lists), or <see langword="null" /> for <c>gpt-5.6-luna</c>, <c>gpt-5.6-terra</c> and <c>gpt-5.6-sol</c>. A reasoning level the chosen model does not accept is lowered to the highest one it does.</param>
public sealed class CodexEngine(string command = "codex", string? configFolder = null, PromptModels? models = null) : IPromptEngine
{
    private readonly PromptModels settings = models ?? new();

    private readonly Lazy<Task<IReadOnlyList<PromptModel>>> catalog = new(() => new CodexModelCatalog(command, configFolder).List());

    private readonly string[] efforts = ["low", "medium", "high", "xhigh", "max", "ultra"];

    private readonly ITurnRunner turns = new TurnRunner();

    private readonly TimeSpan branchTimeout = TimeSpan.FromSeconds(60);

    private readonly string[] access = ["--disable", "shell_tool", "--disable", "multi_agent", "-c", "sandbox_mode=\"read-only\"", "-c", "approval_policy=\"never\""];

    private readonly UTF8Encoding utf8 = new(false);

    private readonly string sessions = Path.Combine(configFolder ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");

    /// <summary>
    /// Gets how long a read of the context window waits for the app server to report it, which it does by itself once a conversation has begun.
    /// </summary>
    internal TimeSpan ContextWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public string Identifier => "Codex";

    /// <inheritdoc />
    public bool CanPersist => true;

    /// <inheritdoc />
    public bool KeepsInstructions => true;

    /// <inheritdoc />
    public bool SchemaPerTurn => true;

    /// <inheritdoc />
    public bool NeedsFiles => true;

    /// <inheritdoc />
    public bool HasSession(string aiSessionId)
        => Directory.Exists(sessions) && Directory.EnumerateFiles(sessions, $"rollout-*-{aiSessionId}.jsonl", SearchOption.AllDirectories).Any();

    /// <inheritdoc />
    public string FormatSchema(JsonElement schema)
    {
        JsonNode node = JsonNode.Parse(schema.GetRawText())!;
        Strict(node.AsObject());
        return node.ToJsonString();
    }

    /// <inheritdoc />
    public ProcessStartInfo CreateStart(PromptEngineStart start)
        => throw new NotSupportedException($"{Identifier} is driven through its app server.");

    /// <inheritdoc />
    public ProcessStartInfo CreateAttachmentStart(PromptEngineStart start)
        => throw new NotSupportedException($"{Identifier} is driven through its app server.");

    /// <inheritdoc />
    public bool Accepts(string mediaType)
        => mediaType.IsImage();

    /// <inheritdoc />
    public string Frame(PromptInput input)
        => throw new NotSupportedException($"{Identifier} is driven through its app server.");

    /// <inheritdoc />
    public string Plain(PromptInput input)
        => throw new NotSupportedException($"{Identifier} is driven through its app server.");

    /// <inheritdoc />
    public IPromptProcess Attach(Process process, PromptEngineStart start)
        => new CodexProcess(this, process, start);

    /// <inheritdoc />
    public ProcessStartInfo CreatePersistentStart(PromptEngineStart given)
    {
        ProcessStartInfo start = new(command) { WorkingDirectory = given.WorkingFolder };

        foreach (string argument in (string[])["app-server", .. access, .. given.Servers.SelectMany(Server)])
        {
            start.ArgumentList.Add(argument);
        }

        if (configFolder is not null)
        {
            start.Environment["CODEX_HOME"] = configFolder;
        }

        if (given.ApiKey is not null)
        {
            start.Environment["CODEX_API_KEY"] = given.ApiKey;
        }

        foreach (PromptEngineServer server in given.Servers)
        {
            if (server.BearerTokenVariable is { } variable && server.BearerToken is { } token)
            {
                start.Environment[variable] = token;
            }
        }

        return start;
    }

    /// <inheritdoc />
    public async Task<PromptBranch> Branch(PromptEngineStart source, PromptHistoryPoint? at, CancellationToken cancellation = default)
    {
        if (source.AiSessionId is not { } origin)
        {
            return new PromptBranch();
        }

        JsonObject parameters = new() { ["threadId"] = origin };

        if (at is not null)
        {
            parameters["lastTurnId"] = await TurnOf(Find(origin) ?? throw new PromptSessionException($"The Codex session {origin} to branch from was not found."), at.Value.Position, cancellation);
        }

        string[] requests =
        [
            """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"Markwardt.LiveAi","version":"1"}}}""",
            """{"method":"initialized"}""",
            new JsonObject { ["id"] = 2, ["method"] = "thread/fork", ["params"] = parameters }.ToJsonString()
        ];

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(branchTimeout);

        try
        {
            await foreach (string line in turns.Exchange(Identifier, AppServer(source.ApiKey), requests, false, limit.Token))
            {
                (bool answered, string? error, string? id) = Forked(line);

                if (answered)
                {
                    return error is not null ? throw new PromptSessionException(error) : new PromptBranch { AiSessionId = id ?? throw new PromptSessionException("Codex did not report the session of the branch.") };
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new PromptSessionException("Codex did not branch the session in time.");
        }

        throw new PromptSessionException("Codex stopped before it branched the session.");
    }

    /// <inheritdoc />
    public Task DeleteSession(string aiSessionId, CancellationToken cancellation = default)
    {
        if (Find(aiSessionId) is not { } path)
        {
            return Task.CompletedTask;
        }

        File.Delete(path);
        string root = Path.GetFullPath(sessions);

        for (string? folder = Path.GetDirectoryName(path); folder is not null && Path.GetFullPath(folder).Length > root.Length && !Directory.EnumerateFileSystemEntries(folder).Any(); folder = Path.GetDirectoryName(folder))
        {
            Directory.Delete(folder);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptHistoryEntry> ReadHistory(string aiSessionId, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        string? path = Find(aiSessionId);

        if (path is null)
        {
            yield break;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        await foreach ((long offset, string line) in new ReverseLineReader().Read(stream, cancellation))
        {
            IReadOnlyList<PromptHistoryEntry> entries = [.. Entries(line)];

            for (int index = entries.Count - 1; index >= 0; index--)
            {
                yield return entries[index] with { Point = new PromptHistoryPoint(offset, index) };
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PromptEngineEvent> Parse(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type))
            {
                return [];
            }

            switch (type.GetString())
            {
                case "thread.started" when root.TryGetProperty("thread_id", out JsonElement thread) && thread.GetString() is { Length: > 0 } id:
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = id }];
                case "item.completed" when root.GetProperty("item") is { } item && item.TryGetProperty("type", out JsonElement kind) && kind.GetString() == "agent_message" && item.TryGetProperty("text", out JsonElement reply) && !string.IsNullOrWhiteSpace(reply.GetString()):
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = reply.GetString()! }];
                case "item.started" when root.GetProperty("item") is { } started && ItemKind(started) == "mcp_tool_call":
                    return [Event(Call(started))];
                case "item.completed" when root.GetProperty("item") is { } finished && ItemKind(finished) == "mcp_tool_call":
                    return [Event(Result(finished))];
                case "turn.completed":
                    return root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object
                        ? [Event(new PromptUsageEvent { Usage = Usage(usage) }), new PromptEngineEvent { Kind = PromptEngineEventKind.Finished }]
                        : [new PromptEngineEvent { Kind = PromptEngineEventKind.Finished }];
                case "turn.failed":
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = Failure(root) }];
                default:
                    return [];
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads how much of the subscription is used, from the app server and without sending a prompt.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The limit windows, one or two depending on the plan.</returns>
    /// <exception cref="PromptSessionException">The tool reported no limits (an API key has none), did not answer in time or could not be started.</exception>
    public Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default)
        => new CodexLimits(command, configFolder).Read(cancellation);

    /// <summary>
    /// Describes the smallest process that proves the tool works: the cheapest model, no tools and nothing stored. The prompt is written to its standard input.
    /// </summary>
    /// <param name="apiKey">The API key to run with, or <see langword="null" /> to use the configured sign in.</param>
    /// <returns>The process to start, without its streams redirected.</returns>
    public ProcessStartInfo CreateTestStart(string? apiKey)
    {
        ProcessStartInfo start = new(command) { WorkingDirectory = Path.GetTempPath() };

        foreach (string argument in (string[])["exec", "--json", "--skip-git-repo-check", "--ephemeral", "-m", Model(PromptCapability.Fast), "-c", $"model_reasoning_effort=\"{Effort(PromptReasoning.Low)}\"", .. access, "-"])
        {
            start.ArgumentList.Add(argument);
        }

        if (configFolder is not null)
        {
            start.Environment["CODEX_HOME"] = configFolder;
        }

        if (apiKey is not null)
        {
            start.Environment["CODEX_API_KEY"] = apiKey;
        }

        return start;
    }

    private string? Find(string aiSessionId)
        => Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, $"rollout-*-{aiSessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault() : null;

    /// <summary>
    /// Reads what a notification of the app server reports about the items of a turn.
    /// </summary>
    /// <param name="method">The method of the notification.</param>
    /// <param name="values">The parameters of the notification.</param>
    /// <param name="turn">The turn that is running, whose items are the only ones read.</param>
    /// <returns>The events, which are none for a notification that is not of interest.</returns>
    internal IReadOnlyList<PromptEngineEvent> Read(string method, JsonElement values, string? turn)
    {
        try
        {
            if (values.TryGetProperty("turnId", out JsonElement owner) && owner.GetString() != turn)
            {
                return [];
            }

            switch (method)
            {
                case "item/agentMessage/delta" when values.TryGetProperty("delta", out JsonElement delta) && delta.GetString() is { Length: > 0 } text:
                    return [Event(new PromptDeltaEvent { Text = text })];
                case "item/completed" when values.TryGetProperty("item", out JsonElement finished) && ItemKind(finished) == "agentMessage" && finished.TryGetProperty("text", out JsonElement reply) && !string.IsNullOrWhiteSpace(reply.GetString()):
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = reply.GetString()! }];
                case "item/started" when values.TryGetProperty("item", out JsonElement started) && ItemKind(started) == "mcpToolCall":
                    return [Event(Call(started))];
                case "item/completed" when values.TryGetProperty("item", out JsonElement done) && ItemKind(done) == "mcpToolCall":
                    return [Event(Result(done))];
                default:
                    return [];
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    private PromptEngineEvent Event(PromptEvent output)
        => new() { Kind = PromptEngineEventKind.Event, Event = output };

    private string? ItemKind(JsonElement item)
        => item.TryGetProperty("type", out JsonElement kind) ? kind.GetString() : null;

    private PromptToolCallEvent Call(JsonElement item)
        => new()
        {
            Id = item.GetProperty("id").GetString() ?? string.Empty,
            Server = item.TryGetProperty("server", out JsonElement server) ? server.GetString() : null,
            Name = item.TryGetProperty("tool", out JsonElement tool) ? tool.GetString() ?? string.Empty : string.Empty,
            Input = item.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind != JsonValueKind.Null ? arguments.GetRawText() : "{}"
        };

    private PromptToolResultEvent Result(JsonElement item)
    {
        bool failed = item.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object || item.TryGetProperty("status", out JsonElement status) && status.GetString() == "failed";
        string output = failed && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? string.Empty : item.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array ? string.Join("\n", content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString())) : string.Empty;
        return new PromptToolResultEvent { Id = item.GetProperty("id").GetString() ?? string.Empty, Output = output, IsError = failed };
    }

    private PromptUsage Usage(JsonElement usage)
        => new()
        {
            InputTokens = Count(usage, "input_tokens") - Count(usage, "cached_input_tokens"),
            CachedInputTokens = Count(usage, "cached_input_tokens"),
            OutputTokens = Count(usage, "output_tokens")
        };

    private long Count(JsonElement usage, string name)
        => usage.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private IEnumerable<PromptHistoryEntry> Entries(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("type", out JsonElement kind) || !root.TryGetProperty("type", out JsonElement outer))
            {
                return [];
            }

            DateTimeOffset? time = root.TryGetProperty("timestamp", out JsonElement stamp) && stamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(stamp.GetString(), out DateTimeOffset parsed) ? parsed : null;
            string? message = payload.TryGetProperty("message", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

            return (outer.GetString(), kind.GetString()) switch
            {
                ("event_msg", "user_message") when message is not null => [new PromptHistoryEntry { Event = new PromptInputEvent { Text = message }, Time = time }],
                ("event_msg", "agent_message") when message is not null => [new PromptHistoryEntry { Event = new PromptMessageEvent { Text = message }, Time = time }],
                ("event_msg", "mcp_tool_call_end") => Call(payload, time),
                _ => []
            };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    private IEnumerable<PromptHistoryEntry> Call(JsonElement payload, DateTimeOffset? time)
    {
        JsonElement invocation = payload.GetProperty("invocation");
        string output = string.Empty;
        bool failed = false;

        if (payload.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object)
        {
            output = result.TryGetProperty("Ok", out JsonElement ok) && ok.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array
                ? string.Join("\n", content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString()))
                : result.TryGetProperty("Err", out JsonElement error) ? error.ToString() : string.Empty;
            failed = result.TryGetProperty("Err", out _);
        }

        string id = payload.TryGetProperty("call_id", out JsonElement call) ? call.GetString() ?? string.Empty : string.Empty;

        return
        [
            new PromptHistoryEntry
            {
                Event = new PromptToolCallEvent
                {
                    Id = id,
                    Server = invocation.TryGetProperty("server", out JsonElement server) ? server.GetString() : null,
                    Name = invocation.GetProperty("tool").GetString() ?? string.Empty,
                    Input = invocation.TryGetProperty("arguments", out JsonElement arguments) ? arguments.GetRawText() : "{}"
                },
                Time = time
            },
            new PromptHistoryEntry { Event = new PromptToolResultEvent { Id = id, Output = output, IsError = failed }, Time = time }
        ];
    }

    private string[] Server(PromptEngineServer server)
    {
        string prefix = $"mcp_servers.{server.Name}";
        List<string> arguments = ["-c", $"{prefix}.default_tools_approval_mode=\"approve\""];

        if (server.Url is { } url)
        {
            arguments.AddRange(["-c", $"{prefix}.url={Toml(url)}"]);

            if (server.BearerTokenVariable is { } variable)
            {
                arguments.AddRange(["-c", $"{prefix}.bearer_token_env_var={Toml(variable)}"]);
            }
        }
        else
        {
            arguments.AddRange(["-c", $"{prefix}.command={Toml(server.Command!)}", "-c", $"{prefix}.args=[{string.Join(", ", server.Arguments.Select(Toml))}]"]);

            if (server.EnvironmentVariables.Count > 0)
            {
                arguments.AddRange(["-c", $"{prefix}.env={{{string.Join(", ", server.EnvironmentVariables.Select(entry => $"{Toml(entry.Key)} = {Toml(entry.Value)}"))}}}"]);
            }
        }

        if (server.BearerToken is not null)
        {
            arguments.AddRange(["-c", $"{prefix}.tool_timeout_sec=3700"]);
        }

        return [.. arguments];
    }

    private (bool Answered, string? Error, string? Id) Forked(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _) || !root.TryGetProperty("id", out JsonElement found) || found.ValueKind != JsonValueKind.Number || !found.TryGetInt32(out int number) || number != 2)
            {
                return (false, null, null);
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                return (true, error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message) && message.GetString() is { Length: > 0 } text ? text : "Codex could not branch the session.", null);
            }

            return (true, null, root.TryGetProperty("result", out JsonElement result) && result.TryGetProperty("thread", out JsonElement thread) && thread.TryGetProperty("id", out JsonElement id) ? id.GetString() : null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return (false, null, null);
        }
    }

    private async Task<string> TurnOf(string path, long position, CancellationToken cancellation = default)
    {
        string? turn = null;

        foreach ((long offset, string line) in await new ForwardLineReader().Read(path, cancellation))
        {
            if (turn is not null && offset > position)
            {
                break;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out JsonElement type) && type.GetString() == "event_msg" && root.TryGetProperty("payload", out JsonElement payload) && payload.TryGetProperty("type", out JsonElement kind) && kind.GetString() == "task_started" && payload.TryGetProperty("turn_id", out JsonElement id) && id.GetString() is { Length: > 0 } found)
                {
                    turn = found;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
            }
        }

        return turn ?? throw new PromptSessionException("The conversation has no turn to branch at.");
    }

    private ProcessStartInfo AppServer(string? apiKey)
    {
        ProcessStartInfo start = new(command) { WorkingDirectory = Path.GetTempPath() };
        start.ArgumentList.Add("app-server");

        if (configFolder is not null)
        {
            start.Environment["CODEX_HOME"] = configFolder;
        }

        if (apiKey is not null)
        {
            start.Environment["CODEX_API_KEY"] = apiKey;
        }

        return start;
    }

    private string Toml(string text)
    {
        StringBuilder builder = new("\"");

        foreach (char letter in text)
        {
            builder.Append(letter switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or '\u007f' => $"\\u{(int)letter:X4}",
                _ => letter.ToString()
            });
        }

        return builder.Append('"').ToString();
    }

    private void Strict(JsonObject schema)
    {
        foreach ((string key, JsonNode? value) in schema.ToList())
        {
            switch (value)
            {
                case JsonObject map when key is "properties" or "$defs" or "definitions" or "patternProperties":
                    foreach (JsonObject child in map.Select(entry => entry.Value).OfType<JsonObject>())
                    {
                        Strict(child);
                    }

                    break;
                case JsonObject child:
                    Strict(child);
                    break;
                case JsonArray children:
                    foreach (JsonObject child in children.OfType<JsonObject>())
                    {
                        Strict(child);
                    }

                    break;
            }
        }

        if (schema["properties"] is JsonObject properties)
        {
            schema["additionalProperties"] = false;
            schema["required"] = new JsonArray(properties.Select(entry => (JsonNode?)JsonValue.Create(entry.Key)).ToArray());
        }
    }

    /// <summary>
    /// Names the model that runs a capability.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <returns>The name the tool takes.</returns>
    internal string Model(PromptCapability capability)
        => settings.For(capability) ?? capability switch
        {
            PromptCapability.Fast => "gpt-5.6-luna",
            PromptCapability.Balanced => "gpt-5.6-terra",
            PromptCapability.Max => "gpt-5.6-sol",
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };

    /// <summary>
    /// Names the effort that a level of reasoning is.
    /// </summary>
    /// <param name="reasoning">The reasoning.</param>
    /// <returns>The name the tool takes.</returns>
    internal string Effort(PromptReasoning reasoning)
        => reasoning switch
        {
            PromptReasoning.Low => "low",
            PromptReasoning.Medium => "medium",
            PromptReasoning.High => "high",
            PromptReasoning.Max => "max",
            _ => throw new ArgumentOutOfRangeException(nameof(reasoning), reasoning, null)
        };

    /// <summary>
    /// Names the effort a model is run at: the one the level of reasoning is, or the highest one below it that the model accepts when the model is one the caller chose and the catalog says it does not accept that one.
    /// </summary>
    /// <param name="capability">The capability whose model runs.</param>
    /// <param name="reasoning">The reasoning.</param>
    /// <param name="cancellation">Cancels the lookup.</param>
    /// <returns>The name the tool takes.</returns>
    internal async Task<string> Effort(PromptCapability capability, PromptReasoning reasoning, CancellationToken cancellation = default)
    {
        string wanted = Effort(reasoning);

        if (settings.For(capability) is not { } model)
        {
            return wanted;
        }

        IReadOnlyList<string>? accepted = (await catalog.Value.WaitAsync(cancellation)).FirstOrDefault(listed => listed.Id == model)?.Efforts;

        if (accepted is not { Count: > 0 } || accepted.Contains(wanted))
        {
            return wanted;
        }

        int rank = Array.IndexOf(efforts, wanted);
        string[] known = [.. accepted.Where(effort => Array.IndexOf(efforts, effort) >= 0).OrderBy(effort => Array.IndexOf(efforts, effort))];
        return known.LastOrDefault(effort => Array.IndexOf(efforts, effort) <= rank) ?? known.FirstOrDefault() ?? wanted;
    }

    /// <summary>
    /// Finds the message of a failed turn.
    /// </summary>
    /// <param name="turn">The turn, whose error is read.</param>
    /// <returns>The message.</returns>
    internal string Failure(JsonElement turn)
    {
        string message = turn.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } found ? found : "Codex failed.";

        try
        {
            using JsonDocument inner = JsonDocument.Parse(message);
            return inner.RootElement.ValueKind == JsonValueKind.Object && inner.RootElement.TryGetProperty("error", out JsonElement detail) && detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("message", out JsonElement nested) && nested.ValueKind == JsonValueKind.String && nested.GetString() is { Length: > 0 } unwrapped ? unwrapped : message;
        }
        catch (JsonException)
        {
            return message;
        }
    }
}
