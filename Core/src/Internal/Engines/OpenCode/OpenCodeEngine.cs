namespace Markwardt.LiveAi;

/// <summary>
/// OpenCode 2.x, driven through the HTTP server it starts with <c>opencode serve --stdio</c>. OpenCode runs models from many providers, among them models on this machine served by Ollama, llama.cpp or LM Studio (see <paramref name="localServers" />), so this is the engine for local models. Its tools are the MCP servers of the session only: its own file, shell and web tools are turned off, because the library serves the file tools and the shell itself.
/// </summary>
/// <param name="command">The command that starts OpenCode: <c>opencode</c> found on the <c>PATH</c>, or the path of another one.</param>
/// <param name="localServers">The addresses of the OpenAI compatible APIs of the servers on this machine whose models are offered (the identifier of a model is <c>provider/model</c>, with <c>ollama</c> as the provider of Ollama on its usual port), or <see langword="null" /> for Ollama on <c>http://localhost:11434/v1</c>. Give an empty list to offer none. The models are listed when asked, so a server that is started later is found.</param>
/// <param name="providers">Model providers to add to OpenCode's configuration, besides the servers above and the ones its own configuration files define, or <see langword="null" /> for none. Their models are offered as <c>provider/model</c>.</param>
/// <param name="preferredModels">The model to run at each capability, or <see langword="null" /> to pick by capability.</param>
/// <param name="contexts">The size of the context window to run the model of each capability with, or <see langword="null" /> for the size the server runs them with.</param>
internal sealed class OpenCodeEngine(string command = "opencode", IEnumerable<string>? localServers = null, IEnumerable<OpenCodeModelProvider>? providers = null, PromptCapabilityModels? preferredModels = null, PromptCapabilityContexts? contexts = null) : IPromptEngine
{
    private readonly ITurnRunner turns = new TurnRunner();

    private readonly List<ILocalServer> servers = [.. (localServers ?? ["http://localhost:11434/v1"]).Select(url => (ILocalServer)new LocalServer(url))];

    private readonly List<OpenCodeModelProvider> extra = [.. providers ?? []];

    private readonly TimeSpan exchangeTimeout = TimeSpan.FromSeconds(60);

    private readonly string instructions = Path.Combine(Path.GetTempPath(), "markwardt.liveai-opencode");

    /// <summary>
    /// Gets how long a start waits for the MCP servers of the session to connect before it fails or goes on without the optional ones.
    /// </summary>
    internal TimeSpan ConnectWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets how often a start asks the server for the state of its MCP servers.
    /// </summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc />
    public string Identifier => "OpenCode";

    /// <inheritdoc />
    public PromptProvider Provider => PromptProvider.Local;

    /// <inheritdoc />
    public bool CanPersist => true;

    /// <inheritdoc />
    public bool KeepsInstructions => false;

    /// <inheritdoc />
    public bool SchemaPerTurn => true;

    /// <inheritdoc />
    public bool NeedsFiles => false;

    /// <inheritdoc />
    public bool StartsWithModel => true;

    /// <inheritdoc />
    public PromptCapabilityModels? PreferredModels => preferredModels;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? ModelOverrides(PromptCapability capability)
        => contexts?.For(capability) is { } size ? new Dictionary<string, string> { ["context"] = size.ToString(CultureInfo.InvariantCulture) } : null;

    /// <inheritdoc />
    public bool Accepts(string mediaType)
        => mediaType.IsImage();

    /// <inheritdoc />
    public ProcessStartInfo CreateStart(PromptEngineStart start)
        => throw new NotSupportedException($"{Identifier} is driven through its HTTP server.");

    /// <inheritdoc />
    public ProcessStartInfo CreateAttachmentStart(PromptEngineStart start)
        => throw new NotSupportedException($"{Identifier} is driven through its HTTP server.");

    /// <inheritdoc />
    public ProcessStartInfo CreatePersistentStart(PromptEngineStart given)
    {
        ProcessStartInfo start = WithEnvironment(new ProcessStartInfo(command) { WorkingDirectory = given.WorkingFolder }, given, true);
        start.Environment["OPENCODE_SERVER_PASSWORD"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

        foreach (string argument in (string[])["serve", "--stdio", "--port", "0", "--hostname", "127.0.0.1"])
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <inheritdoc />
    public IPromptProcess Attach(Process process, PromptEngineStart start)
        => new OpenCodeProcess(this, process, start);

    /// <inheritdoc />
    public async Task<PromptBranch> Branch(PromptEngineStart source, PromptHistoryPoint? at, CancellationToken cancellation = default)
    {
        if ((source.AiSessionId ?? source.ForkedFrom) is not { } origin)
        {
            return new PromptBranch();
        }

        JsonObject body = [];

        if (at is { } point && Boundary(await Export(origin, cancellation), point) is { } before)
        {
            body["before"] = before;
        }

        string output = await Command(["api", "--standalone", "POST", $"/api/session/{origin}/fork", "-d", body.ToJsonString()], source.WorkingFolder, cancellation);

        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("id", out JsonElement id) && id.GetString() is { Length: > 0 } forked)
            {
                return new PromptBranch { AiSessionId = forked };
            }

            throw new PromptSessionException(ErrorText(root));
        }
        catch (JsonException)
        {
            throw new PromptSessionException(output.Length > 0 ? output : $"{Identifier} did not report the session of the branch.");
        }
    }

    /// <inheritdoc />
    public async Task DeleteSession(string aiSessionId, CancellationToken cancellation = default)
        => _ = await Command(["session", "delete", "--standalone", aiSessionId], Path.GetTempPath(), cancellation);

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptHistoryEntry> ReadHistory(string aiSessionId, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        IReadOnlyList<JsonElement> messages = await Export(aiSessionId, cancellation);

        foreach (JsonElement message in messages.Reverse())
        {
            IReadOnlyList<PromptHistoryEntry> entries = [.. Entries(message)];
            long position = message.TryGetProperty("time", out JsonElement time) && time.TryGetProperty("created", out JsonElement created) && created.TryGetInt64(out long milliseconds) ? milliseconds : 0;

            for (int index = entries.Count - 1; index >= 0; index--)
            {
                yield return entries[index] with { Point = new PromptHistoryPoint(position, index) };
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

            JsonElement part = root.TryGetProperty("part", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? found : default;

            switch (type.GetString())
            {
                case "text" when part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out JsonElement text) && text.GetString() is { Length: > 0 } reply:
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = reply }];
                case "step_finish" when part.ValueKind == JsonValueKind.Object && part.TryGetProperty("reason", out JsonElement reason) && reason.GetString() == "stop":
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Event, Event = new PromptUsageEvent { Usage = Spent(part) } }, new PromptEngineEvent { Kind = PromptEngineEventKind.Finished }];
                case "error":
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = ErrorText(root) }];
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
    /// Lists the models OpenCode can run: those of the local servers, then those of the model providers it was given, then those of OpenCode's own configuration (the models of a provider it is signed in to; its built-in free hosted models are left out, since they are not on this machine).
    /// </summary>
    /// <param name="apiKey">The API key the models are for, which OpenCode's own configuration does not depend on.</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models, with the settings each one offers.</returns>
    public async Task<IReadOnlyList<PromptModel>> GetModels(string? apiKey = null, CancellationToken cancellation = default)
    {
        List<PromptModel> models = [.. await Local(cancellation), .. Described()];
        models.AddRange(await Configured(cancellation));
        return models;
    }

    /// <summary>
    /// Describes the smallest process that proves the tool works: the first model on this machine (else the first one OpenCode lists) at its lowest settings. The prompt is written to its standard input and the output is read with <see cref="Parse" />.
    /// </summary>
    /// <param name="apiKey">The API key to run with, or <see langword="null" /> to use the configured sign in.</param>
    /// <param name="cancellation">Cancels the choice of the model.</param>
    /// <returns>The process to start, without its streams redirected.</returns>
    public async Task<ProcessStartInfo> CreateTestStart(string? apiKey, CancellationToken cancellation = default)
    {
        PromptModel? model = (await Local(cancellation)).Concat(Described()).FirstOrDefault() ?? (await Configured(cancellation)).FirstOrDefault();
        PromptEngineStart given = new() { Model = model?.Id, Settings = model?.Lowest() ?? [], WorkingFolder = Path.GetTempPath(), ApiKey = apiKey, Instructions = string.Empty };
        ProcessStartInfo start = WithEnvironment(new ProcessStartInfo(command) { WorkingDirectory = Path.GetTempPath() }, given, false);

        foreach (string argument in (string[])["run", "--standalone", "--format", "json"])
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>
    /// Reads how full the context is from the last answer of the conversation, as exported by the tool.
    /// </summary>
    /// <param name="aiSessionId">The session of the tool.</param>
    /// <param name="limit">The size of the context window, or zero when it is not known.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The tokens in the context and the limit, which is zero when it is not known.</returns>
    internal async Task<PromptContext> ReadContext(string aiSessionId, long limit, CancellationToken cancellation = default)
    {
        foreach (JsonElement message in (await Export(aiSessionId, cancellation)).Reverse())
        {
            if (message.TryGetProperty("type", out JsonElement type) && type.GetString() == "assistant" && message.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                long cached = tokens.TryGetProperty("cache", out JsonElement cache) ? Count(cache, "read") : 0;
                return new PromptContext { Tokens = Count(tokens, "input") + cached + Count(tokens, "output"), Limit = limit };
            }
        }

        return new PromptContext { Tokens = 0, Limit = limit };
    }

    /// <summary>
    /// Reads the history entries of one message of an exported conversation.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The entries in order, which are none for a message that holds nothing to show.</returns>
    internal IReadOnlyList<PromptHistoryEntry> Entries(JsonElement message)
    {
        DateTimeOffset? time = message.TryGetProperty("time", out JsonElement stamp) && stamp.TryGetProperty("created", out JsonElement created) && created.TryGetInt64(out long milliseconds) ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
        string? kind = message.TryGetProperty("type", out JsonElement type) ? type.GetString() : null;

        if (kind == "user")
        {
            return message.TryGetProperty("text", out JsonElement text) && text.GetString() is { Length: > 0 } said ? [new PromptHistoryEntry { Event = new PromptInputEvent { Text = said }, Time = time }] : [];
        }

        if (kind != "assistant" || !message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PromptHistoryEntry> entries = [];

        foreach (JsonElement item in content.EnumerateArray())
        {
            string? itemKind = item.TryGetProperty("type", out JsonElement itemType) ? itemType.GetString() : null;

            if (itemKind == "text" && item.TryGetProperty("text", out JsonElement reply) && reply.GetString() is { Length: > 0 } written)
            {
                entries.Add(new PromptHistoryEntry { Event = new PromptMessageEvent { Text = written }, Time = time });
            }
            else if (itemKind == "tool")
            {
                entries.AddRange(Call(item, time));
            }
        }

        return entries;
    }

    /// <summary>
    /// Reads the tokens a step used.
    /// </summary>
    /// <param name="values">The object that holds the <c>tokens</c>.</param>
    /// <returns>The usage, which is empty when there are no tokens.</returns>
    internal PromptUsage Spent(JsonElement values)
    {
        JsonElement tokens = values.TryGetProperty("tokens", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? found : default;
        long cached = tokens.ValueKind == JsonValueKind.Object && tokens.TryGetProperty("cache", out JsonElement cache) ? Count(cache, "read") : 0;
        return tokens.ValueKind == JsonValueKind.Object ? new PromptUsage { InputTokens = Count(tokens, "input"), CachedInputTokens = cached, OutputTokens = Count(tokens, "output") } : new PromptUsage();
    }

    /// <summary>
    /// Reads a token count.
    /// </summary>
    /// <param name="values">The object that holds the count.</param>
    /// <param name="name">The name of the count.</param>
    /// <returns>The count, or zero when it is not there.</returns>
    internal long Count(JsonElement values, string name)
        => values.ValueKind == JsonValueKind.Object && values.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) ? count : 0;

    /// <summary>
    /// Finds the message of an error the tool reported.
    /// </summary>
    /// <param name="root">The event or the object that holds the error.</param>
    /// <returns>The message, or a general one when there is none.</returns>
    internal string ErrorText(JsonElement root)
    {
        JsonElement error = root.TryGetProperty("error", out JsonElement found) ? found : root;

        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? $"{Identifier} failed.";
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in (string[])["message", "name"])
            {
                if (error.TryGetProperty(name, out JsonElement text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } reason)
                {
                    return reason;
                }
            }

            if (error.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out JsonElement detail) && detail.GetString() is { Length: > 0 } explained)
            {
                return explained;
            }
        }

        return $"{Identifier} failed.";
    }

    /// <summary>
    /// Makes the prefix OpenCode puts in front of the tools of an MCP server: the name with every character that is not a letter, a digit, <c>_</c> or <c>-</c> replaced by <c>_</c>.
    /// </summary>
    /// <param name="server">The name of the server.</param>
    /// <returns>The prefix, without its separating underscore.</returns>
    internal string ToolPrefix(string server)
        => new([.. server.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_')]);

    /// <summary>
    /// Finds the message to fork before so that the conversation ends with the exchange of a point.
    /// </summary>
    /// <param name="messages">The messages of the exported conversation.</param>
    /// <param name="point">The point, whose position is the time its message was created.</param>
    /// <returns>The identifier of the first input after the point's exchange, or <see langword="null" /> when the point is in the last exchange.</returns>
    /// <exception cref="PromptSessionException">The point is not in the conversation.</exception>
    internal string? Boundary(IReadOnlyList<JsonElement> messages, PromptHistoryPoint point)
    {
        int index = -1;

        for (int position = 0; position < messages.Count; position++)
        {
            if (Created(messages[position]) <= point.Position)
            {
                index = position;
            }
        }

        if (index < 0)
        {
            throw new PromptSessionException($"The point is not in the conversation of the {Identifier} session.");
        }

        for (int position = index + 1; position < messages.Count; position++)
        {
            if (messages[position].TryGetProperty("type", out JsonElement type) && type.GetString() == "user" && messages[position].TryGetProperty("id", out JsonElement id))
            {
                return id.GetString();
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<JsonElement>> Export(string aiSessionId, CancellationToken cancellation = default)
    {
        ProcessStartInfo start = WithEnvironment(new ProcessStartInfo(command) { WorkingDirectory = Path.GetTempPath() }, new PromptEngineStart { WorkingFolder = Path.GetTempPath(), Instructions = string.Empty }, false);

        foreach (string argument in (string[])["session", "export", "--standalone", aiSessionId])
        {
            start.ArgumentList.Add(argument);
        }

        StringBuilder text = new();

        await foreach (string line in turns.Exchange(Identifier, start, [], true, cancellation))
        {
            text.AppendLine(line);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text.ToString());
            return document.RootElement.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array ? [.. messages.EnumerateArray().Select(message => message.Clone())] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private long Created(JsonElement message)
        => message.TryGetProperty("time", out JsonElement time) && time.TryGetProperty("created", out JsonElement created) && created.TryGetInt64(out long milliseconds) ? milliseconds : long.MaxValue;

    private async Task<string> Command(IReadOnlyList<string> arguments, string folder, CancellationToken cancellation = default)
    {
        ProcessStartInfo start = WithEnvironment(new ProcessStartInfo(command) { WorkingDirectory = folder }, new PromptEngineStart { WorkingFolder = folder, Instructions = string.Empty }, false);

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        StringBuilder text = new();
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(exchangeTimeout);

        try
        {
            await foreach (string line in turns.Exchange(Identifier, start, [], true, limit.Token))
            {
                text.AppendLine(line);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new PromptSessionException($"{Identifier} did not answer in time.");
        }

        return text.ToString().Trim();
    }

    private ProcessStartInfo WithEnvironment(ProcessStartInfo start, PromptEngineStart given, bool withServers)
    {
        start.Environment["OPENCODE_CONFIG_CONTENT"] = Config(given, withServers).ToJsonString();
        start.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
        start.Environment["OPENCODE_DISABLE_CLAUDE_CODE"] = "1";
        start.Environment["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1";

        if (withServers)
        {
            foreach (PromptEngineServer server in given.Servers)
            {
                if (server.BearerTokenVariable is { } variable && server.BearerToken is { } token)
                {
                    start.Environment[variable] = token;
                }
            }
        }

        return start;
    }

    private JsonObject Config(PromptEngineStart given, bool withServers)
    {
        JsonObject config = new() { ["update"] = "disable", ["share"] = "disabled", ["snapshots"] = false, ["websearch"] = false };

        if (given.Model is { } model)
        {
            config["model"] = model;
        }

        JsonObject providers = [];

        foreach (OpenCodeModelProvider provider in extra)
        {
            providers[provider.Id] = Describe(provider, given.ApiKey);
        }

        if (LocalProvider(given) is { } local)
        {
            providers[local.Id] = Describe(local, given.ApiKey);
        }

        if (providers.Count > 0)
        {
            config["provider"] = providers;
        }

        if (!withServers)
        {
            return config;
        }

        JsonObject servers = [];
        JsonArray permissions = new(new JsonObject { ["action"] = "*", ["resource"] = "*", ["effect"] = "deny" });

        foreach (PromptEngineServer server in given.Servers)
        {
            servers[server.Name] = Describe(server);
            permissions.Add(new JsonObject { ["action"] = $"{ToolPrefix(server.Name)}_*", ["resource"] = "*", ["effect"] = "allow" });
        }

        if (servers.Count > 0)
        {
            config["mcp"] = new JsonObject { ["servers"] = servers };
        }

        config["permissions"] = permissions;

        if (!string.IsNullOrWhiteSpace(given.Instructions))
        {
            config["instructions"] = new JsonArray(Instructions(given.Instructions));
        }

        return config;
    }

    private async Task<List<PromptModel>> Local(CancellationToken cancellation)
        => [.. (await Task.WhenAll(servers.Select(server => server.List(cancellation)))).SelectMany(models => models)];

    private IEnumerable<PromptModel> Described()
        => extra.SelectMany(provider => provider.Models.Select(model => new PromptModel { Id = $"{provider.Id}/{model.Id}", Name = model.Name ?? model.Id, ContextLimit = model.ContextLimit, Settings = [] }));

    private async Task<IReadOnlyList<PromptModel>> Configured(CancellationToken cancellation)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(await Command(["api", "--standalone", "GET", "/api/model"], Path.GetTempPath(), cancellation));
            JsonElement list = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out JsonElement data) ? data : document.RootElement;
            HashSet<string> ours = [.. servers.Select(server => server.ProviderId), .. extra.Select(provider => provider.Id), "opencode"];

            return list.ValueKind != JsonValueKind.Array ? [] : [.. list.EnumerateArray().Where(model => model.TryGetProperty("providerID", out JsonElement owner) && owner.GetString() is { } id && !ours.Contains(id) && model.TryGetProperty("modelID", out _) && (!model.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind != JsonValueKind.False)).Select(Known)];
        }
        catch (Exception exception) when (exception is PromptSessionException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    private PromptModel Known(JsonElement model)
    {
        string[] variants = model.TryGetProperty("variants", out JsonElement options) && options.ValueKind == JsonValueKind.Array ? [.. options.EnumerateArray().Select(option => option.TryGetProperty("id", out JsonElement name) ? name.GetString() : null).OfType<string>().Where(name => name.Length > 0)] : [];
        JsonElement capabilities = model.TryGetProperty("capabilities", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? found : default;
        string provider = model.GetProperty("providerID").GetString()!;
        string id = model.GetProperty("modelID").GetString()!;

        return new PromptModel
        {
            Id = $"{provider}/{id}",
            Name = model.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : id,
            AcceptsImages = capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Array ? input.EnumerateArray().Any(kind => kind.GetString() == "image") : null,
            CallsTools = capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind is JsonValueKind.True or JsonValueKind.False ? tools.GetBoolean() : null,
            ContextLimit = model.TryGetProperty("limit", out JsonElement limit) && Count(limit, "context") is > 0 and <= int.MaxValue ? (int)Count(limit, "context") : null,
            Settings = variants.Length == 0 ? [] : [PromptModelSetting.Choose("variant", "Reasoning", variants)]
        };
    }

    private OpenCodeModelProvider? LocalProvider(PromptEngineStart given)
    {
        if (given.Model is not { } id)
        {
            return null;
        }

        ILocalServer? server = servers.FirstOrDefault(known => id.StartsWith($"{known.ProviderId}/", StringComparison.Ordinal));

        return server is null ? null : new OpenCodeModelProvider
        {
            Id = server.ProviderId,
            Name = server.ProviderId == "ollama" ? "Ollama" : server.ProviderId,
            BaseUrl = server.BaseUrl,
            Models = [new OpenCodeModel { Id = id[(server.ProviderId.Length + 1)..], ContextLimit = given.Settings.TryGetValue("context", out string? size) && int.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tokens) ? tokens : null }]
        };
    }

    private JsonObject Describe(OpenCodeModelProvider provider, string? apiKey)
    {
        JsonObject models = [];

        foreach (OpenCodeModel model in provider.Models)
        {
            JsonObject description = new() { ["name"] = model.Name ?? model.Id, ["tool_call"] = true };

            if (model.ContextLimit is not null || model.OutputLimit is not null)
            {
                int context = model.ContextLimit ?? 32768;
                description["limit"] = new JsonObject { ["context"] = context, ["output"] = model.OutputLimit ?? Math.Min(8192, context / 2) };
            }

            models[model.Id] = description;
        }

        return new JsonObject
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["name"] = provider.Name ?? provider.Id,
            ["options"] = new JsonObject { ["baseURL"] = provider.BaseUrl, ["apiKey"] = provider.ApiKey ?? apiKey ?? "none" },
            ["models"] = models
        };
    }

    private JsonObject Describe(PromptEngineServer server)
    {
        if (server.Url is { } url)
        {
            JsonObject remote = new() { ["type"] = "remote", ["url"] = url, ["oauth"] = false, ["codemode"] = false };

            if (server.BearerTokenVariable is { } variable)
            {
                remote["headers"] = new JsonObject { ["Authorization"] = $"Bearer {{env:{variable}}}" };
            }

            remote["timeout"] = server.BearerToken is not null ? new JsonObject { ["startup"] = 30_000, ["catalog"] = 30_000, ["execution"] = 3_700_000 } : new JsonObject { ["startup"] = 30_000, ["catalog"] = 30_000 };

            return remote;
        }

        JsonObject local = new() { ["type"] = "local", ["command"] = new JsonArray([.. new[] { server.Command! }.Concat(server.Arguments).Select(argument => JsonValue.Create(argument))]), ["codemode"] = false, ["timeout"] = new JsonObject { ["startup"] = 30_000, ["catalog"] = 30_000 } };

        if (server.EnvironmentVariables.Count > 0)
        {
            local["environment"] = new JsonObject(server.EnvironmentVariables.Select(entry => KeyValuePair.Create<string, JsonNode?>(entry.Key, JsonValue.Create(entry.Value))));
        }

        return local;
    }

    private string Instructions(string text)
    {
        string path = Path.Combine(instructions, $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}.md");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(instructions);
            string pending = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(pending, text);
            File.Move(pending, path, true);
        }

        return path;
    }

    private IEnumerable<PromptHistoryEntry> Call(JsonElement item, DateTimeOffset? time)
    {
        string id = item.TryGetProperty("id", out JsonElement found) ? found.GetString() ?? string.Empty : string.Empty;
        string name = item.TryGetProperty("name", out JsonElement title) ? title.GetString() ?? string.Empty : string.Empty;
        int split = name.IndexOf('_');
        JsonElement state = item.TryGetProperty("state", out JsonElement given) && given.ValueKind == JsonValueKind.Object ? given : default;
        bool failed = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("status", out JsonElement status) && status.GetString() is "error" or "failed";
        string output = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString()))
            : state.ValueKind == JsonValueKind.Object && state.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? string.Empty : string.Empty;

        return
        [
            new PromptHistoryEntry
            {
                Event = new PromptToolCallEvent
                {
                    Id = id,
                    Server = split > 0 ? name[..split] : null,
                    Name = split > 0 ? name[(split + 1)..] : name,
                    Input = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("input", out JsonElement input) ? JsonNode.Parse(input.GetRawText())?.ToJsonString() ?? "{}" : "{}"
                },
                Time = time
            },
            new PromptHistoryEntry { Event = new PromptToolResultEvent { Id = id, Output = output, IsError = failed }, Time = time }
        ];
    }
}
