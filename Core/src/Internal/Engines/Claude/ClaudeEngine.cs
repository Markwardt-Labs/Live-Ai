namespace Markwardt.LiveAi;

/// <summary>
/// Claude Code (<c>claude -p</c>), which runs a prompt in a process of its own or in one that stays and takes prompts as it is given them.
/// </summary>
/// <param name="command">The command that starts Claude Code.</param>
/// <param name="configFolder">The folder Claude Code keeps its state in, or <see langword="null" /> for <c>CLAUDE_CONFIG_DIR</c> or <c>.claude</c> in the user's home folder. It is also passed to the tool as <c>CLAUDE_CONFIG_DIR</c>, so the library and the tool look in the same place.</param>
internal sealed class ClaudeEngine(string command = "claude", string? configFolder = null) : IPromptEngine
{
    private readonly ITurnRunner turns = new TurnRunner();

    private readonly string[] access = ["--permission-mode", "dontAsk", "--tools", string.Empty];

    private readonly string projects = Path.Combine(configFolder ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");

    /// <summary>
    /// Gets how long a listing of the models waits for the tool before it gives none.
    /// </summary>
    internal TimeSpan ModelsTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public string Identifier => "Claude Code";

    /// <inheritdoc />
    public bool CanPersist => true;

    /// <inheritdoc />
    public bool KeepsInstructions => false;

    /// <inheritdoc />
    public bool SchemaPerTurn => false;

    /// <inheritdoc />
    public bool NeedsFiles => false;

    /// <inheritdoc />
    public bool NativeSearch => true;

    /// <summary>
    /// Gets the line that makes the process stop what it is doing and wait for the next prompt.
    /// </summary>
    public string? InterruptFrame => """{"type":"control_request","request_id":"liveai-interrupt","request":{"subtype":"interrupt"}}""";

    /// <inheritdoc />
    public bool HasSession(string aiSessionId)
        => Find(aiSessionId) is not null;

    /// <inheritdoc />
    public string FormatSchema(JsonElement schema)
        => schema.GetRawText();

    /// <inheritdoc />
    public ProcessStartInfo CreateStart(PromptEngineStart start)
        => Start(start, false);

    /// <inheritdoc />
    public ProcessStartInfo CreatePersistentStart(PromptEngineStart start)
        => Start(start, true);

    /// <inheritdoc />
    public bool Accepts(string mediaType)
        => mediaType.IsImage() || mediaType == "application/pdf";

    /// <inheritdoc />
    public ProcessStartInfo CreateAttachmentStart(PromptEngineStart start)
        => Start(start, true);

    /// <inheritdoc />
    public string Frame(PromptInput input)
        => new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = input.Attachments.Count == 0 && input.Notes.Count == 0
                    ? input.Text
                    : new JsonArray([.. input.Notes.Select(note => new JsonObject { ["type"] = "text", ["text"] = Reminder(note) }), .. input.Attachments.Select(Block), new JsonObject { ["type"] = "text", ["text"] = input.Text }])
            }
        }.ToJsonString();

    /// <inheritdoc />
    public string Plain(PromptInput input)
        => input.Notes.Count == 0 ? input.Text : $"{string.Join("\n\n", input.Notes.Select(Reminder))}\n\n{input.Text}";

    /// <inheritdoc />
    public IPromptProcess Attach(Process process, PromptEngineStart start)
        => new ClaudeProcess(this, process, start);

    /// <inheritdoc />
    public async Task<PromptBranch> Branch(PromptEngineStart source, PromptHistoryPoint? at, CancellationToken cancellation = default)
    {
        string? origin = source.AiSessionId ?? source.ForkedFrom;

        if (origin is null)
        {
            return new PromptBranch();
        }

        if (at is null)
        {
            return new PromptBranch { ForkedFrom = origin, ForkedAt = source.AiSessionId is null ? source.ForkedAt : null };
        }

        string path = Find(origin) ?? throw new PromptSessionException($"The Claude Code session {origin} to branch from was not found.");
        return new PromptBranch { ForkedFrom = origin, ForkedAt = await TurnEnd(path, at.Value.Position, cancellation) };
    }

    /// <inheritdoc />
    public Task DeleteSession(string aiSessionId, CancellationToken cancellation = default)
    {
        foreach (string path in FindAll(aiSessionId))
        {
            File.Delete(path);

            if (Path.Combine(Path.GetDirectoryName(path)!, aiSessionId) is { } extras && Directory.Exists(extras))
            {
                Directory.Delete(extras, true);
            }

            if (Path.GetDirectoryName(path) is { } folder && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MoveSession(string aiSessionId, string folder, CancellationToken cancellation = default)
    {
        string target = Path.Combine(projects, Regex.Replace(Path.GetFullPath(folder), "[^a-zA-Z0-9]", "-"));
        string destination = Path.Combine(target, $"{aiSessionId}.jsonl");

        if (File.Exists(destination))
        {
            return Task.CompletedTask;
        }

        string path = Find(aiSessionId) ?? throw new PromptSessionException($"The Claude Code session {aiSessionId} to move was not found.");
        Directory.CreateDirectory(target);
        File.Copy(path, destination);

        if (Path.Combine(Path.GetDirectoryName(path)!, aiSessionId) is { } extras && Directory.Exists(extras))
        {
            CopyFolder(extras, Path.Combine(target, aiSessionId));
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

            bool main = !root.TryGetProperty("parent_tool_use_id", out JsonElement parent) || parent.ValueKind == JsonValueKind.Null;

            switch (type.GetString())
            {
                case "assistant" when main:
                    return Blocks(root.GetProperty("message").GetProperty("content"));
                case "user" when main && root.GetProperty("message").TryGetProperty("content", out JsonElement results) && results.ValueKind == JsonValueKind.Array:
                    return [.. results.EnumerateArray().Where(block => Kind(block) == "tool_result").Select(Result)];
                case "stream_event" when main && root.TryGetProperty("event", out JsonElement streamed) && Delta(streamed) is { } delta:
                    return [Event(new PromptDeltaEvent { Text = delta })];
                case "rate_limit_event" when root.TryGetProperty("rate_limit_info", out JsonElement info) && Windows(info) is { } windows:
                    return [Event(new PromptWindowsEvent { Windows = windows })];
                case "result" when root.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True:
                    return [new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = Failure(root) }];
                case "result":
                    List<PromptEngineEvent> events = [];

                    if (root.TryGetProperty("session_id", out JsonElement session) && session.GetString() is { Length: > 0 } id)
                    {
                        events.Add(new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = id });
                    }

                    if (root.TryGetProperty("structured_output", out JsonElement structured) && structured.ValueKind == JsonValueKind.Object)
                    {
                        events.Add(new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = structured.GetRawText() });
                    }

                    if (Usage(root) is { } usage)
                    {
                        events.Add(Event(new PromptUsageEvent { Usage = usage }));
                    }

                    events.Add(new PromptEngineEvent { Kind = PromptEngineEventKind.Finished });
                    return events;
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
    /// Reads how much of the subscription is used, with the tool's own <c>/usage</c> command and without sending a prompt.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The session, the week and the week of each single model the plan limits.</returns>
    /// <exception cref="PromptSessionException">The tool reported no limits (an API key has none), did not answer in time or could not be started.</exception>
    public Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default)
        => new ClaudeUsage(command, configFolder).Read(cancellation);

    /// <summary>
    /// Lists the models of Claude Code, as the tool itself reports them to a client (the same list its model picker shows, with the effort levels each model accepts). The pinned versions it lists are included, and its own <c>default</c> entry is not, since it is a name for one of the others.
    /// </summary>
    /// <param name="apiKey">The API key the models are for, or <see langword="null" /> for the configured sign in.</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models, or none when the tool could not be asked.</returns>
    public async Task<IReadOnlyList<PromptModel>> GetModels(string? apiKey = null, CancellationToken cancellation = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(ModelsTimeout);

        try
        {
            ProcessStartInfo start = new(command) { WorkingDirectory = Path.GetTempPath() };

            foreach (string argument in (string[])["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--strict-mcp-config", .. access])
            {
                start.ArgumentList.Add(argument);
            }

            Configure(start, apiKey);

            await foreach (string line in turns.Exchange(Identifier, start, ["""{"type":"control_request","request_id":"liveai-models","request":{"subtype":"initialize"}}"""], false, limit.Token))
            {
                if (ReadModels(line) is { } models)
                {
                    return models;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
        }
        catch (PromptSessionException)
        {
        }

        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<PromptModel> FallbackModels { get; } = [.. new[] { "haiku", "sonnet", "opus", "fable" }
        .Select(alias => new PromptModel { Id = alias, Name = PromptDescriptors.Capitalized(alias), AcceptsImages = true, CallsTools = true, Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high", "max"], "medium")] })];

    /// <inheritdoc />
    public Task<ProcessStartInfo> CreateTestStart(string? apiKey, CancellationToken cancellation = default)
    {
        ProcessStartInfo start = new(command) { WorkingDirectory = Path.GetTempPath() };

        foreach (string argument in (string[])["-p", "--output-format", "stream-json", "--verbose", "--model", "haiku", "--effort", "low", "--strict-mcp-config", "--no-session-persistence", "--system-prompt", "Be terse.", .. access])
        {
            start.ArgumentList.Add(argument);
        }

        Configure(start, apiKey);
        return Task.FromResult(start);
    }

    private void Configure(ProcessStartInfo start, string? apiKey)
    {
        if (configFolder is not null)
        {
            start.Environment["CLAUDE_CONFIG_DIR"] = configFolder;
        }

        if (apiKey is not null)
        {
            start.Environment["ANTHROPIC_API_KEY"] = apiKey;
        }
    }

    private IReadOnlyList<PromptModel>? ReadModels(string line)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type) || type.GetString() != "control_response" || !root.TryGetProperty("response", out JsonElement envelope) || envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("request_id", out JsonElement id) || id.GetString() != "liveai-models")
            {
                return null;
            }

            if (!envelope.TryGetProperty("response", out JsonElement body) || body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            try
            {
                return [.. models.EnumerateArray().Where(model => model.ValueKind == JsonValueKind.Object && model.TryGetProperty("value", out JsonElement value) && value.GetString() is { Length: > 0 } name && name != "default").Select(Read)];
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
            {
                return [];
            }
        }
    }

    private PromptModel Read(JsonElement model)
    {
        string[] efforts = model.TryGetProperty("supportedEffortLevels", out JsonElement levels) && levels.ValueKind == JsonValueKind.Array ? [.. levels.EnumerateArray().Select(level => level.GetString()).OfType<string>()] : [];

        return new PromptModel
        {
            Id = model.GetProperty("value").GetString()!,
            Name = model.TryGetProperty("displayName", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            Description = model.TryGetProperty("description", out JsonElement description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
            Settings = efforts.Length == 0 ? [] : [PromptModelSetting.Choose("effort", "Effort", efforts)]
        };
    }

    private string? Find(string aiSessionId)
        => FindAll(aiSessionId).FirstOrDefault();

    private IEnumerable<string> FindAll(string aiSessionId)
        => Directory.Exists(projects) ? Directory.EnumerateFiles(projects, $"{aiSessionId}.jsonl", SearchOption.AllDirectories).ToList() : [];

    private void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }

        foreach (string folder in Directory.EnumerateDirectories(source))
        {
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    private string Reminder(string note)
        => $"<system-reminder>\nNote from the host application: {note}\n</system-reminder>";

    private string? Delta(JsonElement streamed)
        => streamed.ValueKind == JsonValueKind.Object && streamed.TryGetProperty("type", out JsonElement type) && type.GetString() == "content_block_delta" && streamed.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("type", out JsonElement kind) && kind.GetString() == "text_delta" && delta.TryGetProperty("text", out JsonElement text) && text.GetString() is { Length: > 0 } written ? written : null;

    private async Task<string> TurnEnd(string path, long position, CancellationToken cancellation = default)
    {
        List<(long Offset, string Uuid, bool Prompt, bool Answer, bool Boundary)> lines = [];

        foreach ((long offset, string line) in await new ForwardLineReader().Read(path, cancellation))
        {
            if (Message(line) is { } found)
            {
                lines.Add((offset, found.Uuid, found.Prompt, found.Answer, found.Boundary));
            }
        }

        int index = lines.FindLastIndex(line => line.Offset <= position);

        if (index < lines.FindLastIndex(line => line.Boundary))
        {
            throw new PromptSessionException("That point is from before the conversation was compacted, which the tool cannot branch at.");
        }

        int first = index < 0 ? 0 : lines.FindLastIndex(index, line => line.Prompt);
        first = first < 0 ? 0 : first;
        int next = lines.FindIndex(first + 1, line => line.Prompt);
        int last = (next < 0 ? lines.Count : next) - 1;

        for (int candidate = last; candidate >= first; candidate--)
        {
            if (lines[candidate].Answer)
            {
                return lines[candidate].Uuid;
            }
        }

        throw new PromptSessionException("The conversation has nothing to branch at in the exchange of that entry.");
    }

    private (string Uuid, bool Prompt, bool Answer, bool Boundary)? Message(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("uuid", out JsonElement uuid) || uuid.GetString() is not { Length: > 0 } id || !root.TryGetProperty("type", out JsonElement type) || root.TryGetProperty("isSidechain", out JsonElement side) && side.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            if (type.GetString() == "assistant")
            {
                return (id, false, true, false);
            }

            if (type.GetString() == "system")
            {
                return root.TryGetProperty("subtype", out JsonElement subtype) && subtype.GetString() == "compact_boundary" ? (id, false, false, true) : null;
            }

            bool prompt = type.GetString() == "user" && root.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement content) && (content.ValueKind == JsonValueKind.String || content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(block => Kind(block) == "text"));
            return prompt ? (id, true, false, false) : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private PromptEngineEvent Event(PromptEvent output)
        => new() { Kind = PromptEngineEventKind.Event, Event = output };

    private string? Kind(JsonElement block)
        => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out JsonElement kind) ? kind.GetString() : null;

    private IReadOnlyList<PromptEngineEvent> Blocks(JsonElement content)
    {
        List<PromptEngineEvent> events = [];
        List<string> texts = [];

        void Flush()
        {
            if (texts.Count > 0)
            {
                events.Add(new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = string.Join("\n\n", texts) });
                texts.Clear();
            }
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            switch (Kind(block))
            {
                case "text" when block.TryGetProperty("text", out JsonElement text) && !string.IsNullOrWhiteSpace(text.GetString()):
                    texts.Add(text.GetString()!);
                    break;
                case "tool_use":
                    Flush();
                    events.Add(Event(Call(block)));
                    break;
            }
        }

        Flush();
        return events;
    }

    private PromptToolCallEvent Call(JsonElement block)
    {
        string name = block.TryGetProperty("name", out JsonElement found) ? found.GetString() ?? string.Empty : string.Empty;
        string[] parts = name.StartsWith("mcp__", StringComparison.Ordinal) ? name.Split("__", 3) : [];

        return new PromptToolCallEvent
        {
            Id = block.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty,
            Server = parts.Length == 3 ? parts[1] : null,
            Name = parts.Length == 3 ? parts[2] : name,
            Input = block.TryGetProperty("input", out JsonElement input) ? input.GetRawText() : "{}"
        };
    }

    private PromptEngineEvent Result(JsonElement block)
        => Event(new PromptToolResultEvent
        {
            Id = block.TryGetProperty("tool_use_id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty,
            Output = block.TryGetProperty("content", out JsonElement content) ? Text(content) : string.Empty,
            IsError = block.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True
        });

    private string Text(JsonElement content)
        => content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Where(part => Kind(part) == "text").Select(part => part.GetProperty("text").GetString())),
            _ => string.Empty
        };

    private IReadOnlyList<PromptWindow>? Windows(JsonElement info)
    {
        if (!info.TryGetProperty("unifiedWindows", out JsonElement windows) || windows.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        windows.TryGetProperty("five_hour", out JsonElement session);
        windows.TryGetProperty("seven_day", out JsonElement week);

        DateTimeOffset readAt = DateTimeOffset.UtcNow;
        List<PromptWindow> found = [];

        if (Fraction(session) is { } now)
        {
            found.Add(new PromptWindow { Name = "Session", Used = now, Length = TimeSpan.FromHours(5), ReadAt = readAt, ResetsAt = Reset(session) });
        }

        if (Fraction(week) is { } all)
        {
            found.Add(new PromptWindow { Name = "Week", Used = all, Length = TimeSpan.FromDays(7), ReadAt = readAt, ResetsAt = Reset(week) });
        }

        return found;
    }

    private double? Fraction(JsonElement window)
        => window.ValueKind == JsonValueKind.Object && window.TryGetProperty("utilization", out JsonElement used) && used.ValueKind == JsonValueKind.Number ? used.GetDouble() : null;

    private DateTimeOffset? Reset(JsonElement window)
        => window.ValueKind == JsonValueKind.Object && window.TryGetProperty("resetsAt", out JsonElement at) && at.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(at.GetInt64()) : null;

    private PromptUsage? Usage(JsonElement result)
    {
        if (!result.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new PromptUsage
        {
            InputTokens = Count(usage, "input_tokens") + Count(usage, "cache_creation_input_tokens"),
            CachedInputTokens = Count(usage, "cache_read_input_tokens"),
            OutputTokens = Count(usage, "output_tokens"),
            CostUsd = result.TryGetProperty("total_cost_usd", out JsonElement cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null
        };
    }

    private long Count(JsonElement usage, string name)
        => usage.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private IEnumerable<PromptHistoryEntry> Entries(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out JsonElement kind) ? kind.GetString() : null;

            if (type is not ("user" or "assistant") || root.TryGetProperty("isSidechain", out JsonElement side) && side.ValueKind == JsonValueKind.True || root.TryGetProperty("isMeta", out JsonElement meta) && meta.ValueKind == JsonValueKind.True || !root.TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content))
            {
                return [];
            }

            DateTimeOffset? time = root.TryGetProperty("timestamp", out JsonElement stamp) && stamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(stamp.GetString(), out DateTimeOffset parsed) ? parsed : null;
            bool user = type == "user";

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() is { Length: > 0 } plain ? [new PromptHistoryEntry { Event = Said(user, plain), Time = time }] : [];
            }

            return [.. content.EnumerateArray().Select(block => Entry(block, user, time)).OfType<PromptHistoryEntry>()];
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    private PromptEvent Said(bool user, string text)
        => user ? new PromptInputEvent { Text = text } : new PromptMessageEvent { Text = text };

    private PromptHistoryEntry? Entry(JsonElement block, bool user, DateTimeOffset? time)
        => Kind(block) switch
        {
            "text" when block.TryGetProperty("text", out JsonElement text) && !string.IsNullOrWhiteSpace(text.GetString()) => new PromptHistoryEntry { Event = Said(user, text.GetString()!), Time = time },
            "tool_use" => new PromptHistoryEntry { Event = Call(block), Time = time },
            "tool_result" => new PromptHistoryEntry
            {
                Event = new PromptToolResultEvent
                {
                    Id = block.TryGetProperty("tool_use_id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty,
                    Output = block.TryGetProperty("content", out JsonElement content) ? Text(content) : string.Empty,
                    IsError = block.TryGetProperty("is_error", out JsonElement failed) && failed.ValueKind == JsonValueKind.True
                },
                Time = time
            },
            "image" or "document" => new PromptHistoryEntry { Event = Said(user, $"[{Kind(block)}]"), Time = time },
            _ => null
        };

    private ProcessStartInfo Start(PromptEngineStart given, bool persistent)
    {
        ProcessStartInfo start = new(command) { WorkingDirectory = given.WorkingFolder };

        foreach (string argument in (string[])["-p", .. persistent ? (string[])["--input-format", "stream-json", "--replay-user-messages"] : [], "--output-format", "stream-json", "--verbose", "--include-partial-messages", .. given.Model is { } model ? (string[])["--model", model] : [], .. given.Settings.TryGetValue("effort", out string? effort) ? (string[])["--effort", effort] : [], "--strict-mcp-config", "--append-system-prompt", given.Instructions, .. given.Schema is { } schema ? (string[])["--json-schema", schema.GetRawText()] : [], .. Servers(given), .. Access(given.Search), .. Resume(given)])
        {
            start.ArgumentList.Add(argument);
        }

        if (configFolder is not null)
        {
            start.Environment["CLAUDE_CONFIG_DIR"] = configFolder;
        }

        if (given.ApiKey is not null)
        {
            start.Environment["ANTHROPIC_API_KEY"] = given.ApiKey;
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

    private string[] Resume(PromptEngineStart start)
        => start.AiSessionId is { } id ? ["--resume", id, "--system-prompt-snapshot", "off"] : start.ForkedFrom is { } origin ? ["--resume", origin, .. start.ForkedAt is { } at ? (string[])["--resume-session-at", at] : [], "--fork-session", "--system-prompt-snapshot", "off"] : [];

    private string[] Access(bool search)
        => ["--permission-mode", "dontAsk", "--tools", search ? "WebSearch" : string.Empty];

    private string[] Servers(PromptEngineStart start)
    {
        List<string> allowed = [.. start.Servers.SelectMany(server => server.Tools is { } names ? names.Select(name => $"mcp__{server.Name}__{name}") : [$"mcp__{server.Name}"])];

        if (start.Search)
        {
            allowed.Add("WebSearch");
        }

        List<string> arguments = [];

        if (start.Servers.Count > 0)
        {
            arguments.AddRange(["--mcp-config", McpConfig(start.Servers)]);
        }

        if (allowed.Count > 0)
        {
            arguments.AddRange(["--allowedTools", string.Join(',', allowed)]);
        }

        return [.. arguments];
    }

    private JsonNode Block(PromptAttachment attachment)
        => new JsonObject
        {
            ["type"] = attachment.MediaType.IsImage() ? "image" : "document",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = attachment.MediaType, ["data"] = Convert.ToBase64String(attachment.Content.Span) }
        };

    private string McpConfig(IReadOnlyList<PromptEngineServer> given)
    {
        JsonObject servers = [];

        foreach (PromptEngineServer server in given)
        {
            servers[server.Name] = server.Url is { } url ? Remote(url, server) : Local(server);
        }

        return new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    private JsonObject Remote(string url, PromptEngineServer server)
    {
        JsonObject remote = new() { ["type"] = "http", ["url"] = url };

        if (server.BearerTokenVariable is { } variable)
        {
            remote["headers"] = new JsonObject { ["Authorization"] = $"Bearer ${{{variable}}}" };
        }

        return remote;
    }

    private JsonObject Local(PromptEngineServer server)
        => new()
        {
            ["type"] = "stdio",
            ["command"] = server.Command,
            ["args"] = new JsonArray([.. server.Arguments.Select(argument => (JsonNode?)JsonValue.Create(argument))]),
            ["env"] = new JsonObject(server.EnvironmentVariables.Select(entry => KeyValuePair.Create<string, JsonNode?>(entry.Key, JsonValue.Create(entry.Value))))
        };

    private string Failure(JsonElement result)
    {
        if (result.TryGetProperty("result", out JsonElement text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()))
        {
            return text.GetString()!;
        }

        if (result.TryGetProperty("errors", out JsonElement list) && list.ValueKind == JsonValueKind.Array && string.Join("\n", list.EnumerateArray().Select(item => item.GetString())) is { Length: > 0 } joined)
        {
            return joined;
        }

        return "Claude Code failed.";
    }
}
