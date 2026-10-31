namespace Markwardt.LiveAi;

/// <summary>
/// A Claude Code process that reads its prompts as lines on its input and writes its events as lines on its output. The model and the effort are changed while it runs, with requests on its input, instead of by starting another process.
/// </summary>
/// <param name="engine">Frames the prompts and reads what the process writes.</param>
/// <param name="process">The started process, whose streams are redirected.</param>
/// <param name="start">What the process was started for, which says the capability and reasoning it started with.</param>
internal sealed class ClaudeProcess(ClaudeEngine engine, Process process, PromptEngineStart start) : IPromptProcess
{
    private readonly Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

    private readonly TimeSpan grace = TimeSpan.FromSeconds(3);

    private readonly TimeSpan answer = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim writing = new(1, 1);

    private PromptCapability capability = start.Capability;

    private PromptReasoning reasoning = start.Reasoning;

    private decimal cost;

    private int requests;

    private readonly object steering = new();

    private int disposed;

    private bool working;

    private int unconsumed;

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
    public async IAsyncEnumerable<PromptEngineEvent> Run(PromptInput input, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await Retune(input.Capability, input.Reasoning, cancellation);

        await foreach (PromptEngineEvent output in Read(engine.Frame(input), cancellation))
        {
            yield return output;
        }
    }

    /// <inheritdoc />
    public async Task<bool> Steer(PromptInput input, CancellationToken cancellation = default)
    {
        lock (steering)
        {
            if (!working)
            {
                return false;
            }

            unconsumed++;
        }

        try
        {
            await Write(engine.Frame(input), cancellation);
        }
        catch
        {
            lock (steering)
            {
                unconsumed--;
            }

            throw;
        }

        return true;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptEngineEvent> Compact([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await foreach (PromptEngineEvent output in Read(engine.Frame(new PromptInput { Text = "/compact", Capability = capability, Reasoning = reasoning }), cancellation))
        {
            yield return output;
        }
    }

    /// <inheritdoc />
    public async Task<PromptContext> GetContext(CancellationToken cancellation = default)
    {
        JsonElement result = await Control("get_context_usage", [], cancellation);

        if (!result.TryGetProperty("totalTokens", out JsonElement used) || used.ValueKind != JsonValueKind.Number || !used.TryGetInt64(out long tokens))
        {
            throw new PromptSessionException("Claude Code did not report how much of its context window is used.");
        }

        long limit = result.TryGetProperty("maxTokens", out JsonElement max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt64(out long window) ? window : 0;
        return new PromptContext { Tokens = tokens, Limit = limit };
    }

    /// <inheritdoc />
    public Task Interrupt(CancellationToken cancellation = default)
        => Write(engine.InterruptFrame ?? throw new NotSupportedException($"{engine.Identifier} cannot be interrupted without ending its process."), cancellation);

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

    private async IAsyncEnumerable<PromptEngineEvent> Read(string frame, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        bool completed = false;
        bool stale = false;
        bool echoed = false;

        lock (steering)
        {
            working = true;
            unconsumed = 0;
        }

        try
        {
            await Write(frame, cancellation);

            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                if (Replayed(line))
                {
                    if (!echoed)
                    {
                        echoed = true;
                    }
                    else
                    {
                        lock (steering)
                        {
                            unconsumed = Math.Max(0, unconsumed - 1);
                        }
                    }

                    continue;
                }

                List<PromptEngineEvent> parsed = [.. engine.Parse(line).Select(PerPrompt)];

                if (parsed.Any(output => output.Kind is PromptEngineEventKind.Finished or PromptEngineEventKind.Failed))
                {
                    lock (steering)
                    {
                        completed = parsed.Any(output => output.Kind == PromptEngineEventKind.Failed) || unconsumed == 0;
                        stale = completed && unconsumed > 0;
                        working = !completed;
                    }
                }

                foreach (PromptEngineEvent output in parsed.Where(output => completed || output.Kind != PromptEngineEventKind.Finished))
                {
                    yield return output;
                }

                if (completed)
                {
                    yield break;
                }
            }

            await process.WaitForExitAsync(cancellation);
            string detail = (await errors).Trim();
            completed = true;
            yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = detail.Length > 0 ? detail : $"{engine.Identifier} stopped." };
        }
        finally
        {
            lock (steering)
            {
                working = false;
            }

            if (!completed || stale)
            {
                Stop();
            }
        }
    }

    private bool Replayed(string line)
    {
        if (!line.Contains("\"isReplay\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out JsonElement type) && type.GetString() == "user" && document.RootElement.TryGetProperty("isReplay", out JsonElement replay) && replay.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task Retune(PromptCapability wanted, PromptReasoning effort, CancellationToken cancellation = default)
    {
        if (wanted != capability)
        {
            await Control("set_model", new JsonObject { ["model"] = engine.Model(wanted) }, cancellation);
            capability = wanted;
        }

        if (effort != reasoning)
        {
            await Control("apply_flag_settings", new JsonObject { ["settings"] = new JsonObject { ["effortLevel"] = engine.Effort(effort) } }, cancellation);
            reasoning = effort;
        }
    }

    private async Task<JsonElement> Control(string subtype, JsonObject arguments, CancellationToken cancellation = default)
    {
        string id = $"liveai-{Interlocked.Increment(ref requests)}";
        JsonObject request = new() { ["subtype"] = subtype };

        foreach ((string name, JsonNode? value) in arguments.ToArray())
        {
            arguments.Remove(name);
            request[name] = value;
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(answer);

        try
        {
            await Write(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request }.ToJsonString(), limit.Token);

            while (await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (Response(line, id) is { } response)
                {
                    return response;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            Stop();
            throw new PromptSessionException($"{engine.Identifier} did not answer a request in time.");
        }

        string detail = (await errors).Trim();
        throw new PromptSessionException(detail.Length > 0 ? detail : $"{engine.Identifier} stopped.");
    }

    private JsonElement? Response(string line, string id)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type) || type.GetString() != "control_response" || !root.TryGetProperty("response", out JsonElement response) || response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("request_id", out JsonElement found) || found.GetString() != id)
            {
                return null;
            }

            if (response.TryGetProperty("subtype", out JsonElement subtype) && subtype.GetString() == "error")
            {
                throw new PromptSessionException(response.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text ? text : $"{engine.Identifier} refused a request.");
            }

            return response.TryGetProperty("response", out JsonElement result) && result.ValueKind == JsonValueKind.Object ? result.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private PromptEngineEvent PerPrompt(PromptEngineEvent output)
    {
        if (output.Event is not PromptUsageEvent { Usage.CostUsd: { } total } usage)
        {
            return output;
        }

        decimal prompt = Math.Round(total - cost, 8);
        cost = total;
        return output with { Event = usage with { Usage = usage.Usage with { CostUsd = prompt } } };
    }

    private async Task Write(string frame, CancellationToken cancellation = default)
    {
        await writing.WaitAsync(cancellation);

        try
        {
            await process.StandardInput.WriteLineAsync(frame.AsMemory(), cancellation);
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
