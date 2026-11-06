namespace Markwardt.LiveAi;

/// <summary>
/// A server on this machine that runs models (Ollama, LM Studio, llama.cpp, vLLM) and serves them through an OpenAI compatible API.
/// </summary>
internal interface ILocalServer
{
    /// <summary>
    /// Gets the identifier of the server in OpenCode's configuration, which is the first part of the identifier of each of its models.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the address of the server's OpenAI compatible API, including its version path.
    /// </summary>
    string BaseUrl { get; }

    /// <summary>
    /// Lists the models of the server. Ollama is asked through its own API, which also tells what each model can do, so only the models that can call tools are listed (an embedding model, for one, cannot); any other server is asked through the OpenAI compatible <c>models</c> endpoint, which lists names only, so all of its models are listed.
    /// </summary>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models, or none when the server is not running or its answer cannot be read.</returns>
    Task<IReadOnlyList<PromptModel>> List(CancellationToken cancellation = default);
}

/// <inheritdoc />
/// <param name="baseUrl">The address of the server's OpenAI compatible API, including its version path.</param>
/// <param name="http">The client to ask with, or <see langword="null" /> for one of its own.</param>
internal sealed class LocalServer(string baseUrl, HttpClient? http = null) : ILocalServer
{
    private readonly HttpClient client = http ?? new HttpClient();

    private readonly string api = baseUrl.TrimEnd('/');

    private readonly string root = baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? baseUrl.TrimEnd('/')[..^3] : baseUrl.TrimEnd('/');

    private readonly int unknownMaximum = 131072;

    private readonly int preferredContext = 4096;

    /// <summary>
    /// Gets how long the listing waits for the server before it gives none.
    /// </summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string ProviderId
    {
        get
        {
            if (!Uri.TryCreate(api, UriKind.Absolute, out Uri? uri))
            {
                return "local";
            }

            return uri.Port == 11434 && uri.IsLoopback ? "ollama" : uri.IsLoopback ? $"local-{uri.Port}" : $"local-{uri.Host}-{uri.Port}";
        }
    }

    /// <inheritdoc />
    public string BaseUrl => api;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptModel>> List(CancellationToken cancellation = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Timeout);

        try
        {
            return await Ollama(limit.Token) ?? await Compatible(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<PromptModel>?> Ollama(CancellationToken cancellation)
    {
        if (await Get($"{root}/api/tags", cancellation) is not { } tags)
        {
            return null;
        }

        using (tags)
        {
            if (!tags.RootElement.TryGetProperty("models", out JsonElement installed) || installed.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            Dictionary<string, int> loaded = await Loaded(cancellation);
            List<PromptModel> models = [];

            foreach (JsonElement entry in installed.EnumerateArray())
            {
                string? name = entry.TryGetProperty("name", out JsonElement found) ? found.GetString() : null;

                if (name is null || await Capabilities(name, entry, cancellation) is not { } capabilities || !capabilities.Contains("tools"))
                {
                    continue;
                }

                int maximum = entry.TryGetProperty("details", out JsonElement details) && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("context_length", out JsonElement size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt32(out int known) && known > 0 ? known : unknownMaximum;
                int? effective = loaded.TryGetValue(name, out int running) ? running : null;
                PromptModelSetting context = PromptModelSetting.Count("context", "Context window", Math.Min(1024, maximum), maximum, Math.Clamp(effective ?? preferredContext, Math.Min(1024, maximum), maximum));

                models.Add(new PromptModel
                {
                    Id = $"{ProviderId}/{name}",
                    Name = name,
                    AcceptsImages = capabilities.Contains("vision"),
                    CallsTools = true,
                    ContextLimit = effective,
                    MaxContextLimit = maximum,
                    Settings = [context]
                });
            }

            return models;
        }
    }

    private async Task<HashSet<string>?> Capabilities(string name, JsonElement entry, CancellationToken cancellation)
    {
        if (entry.TryGetProperty("capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Array)
        {
            return [.. capabilities.EnumerateArray().Select(capability => capability.GetString()).OfType<string>()];
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/api/show") { Content = new StringContent(new JsonObject { ["model"] = name }.ToJsonString(), Encoding.UTF8, "application/json") };
        using HttpResponseMessage response = await client.SendAsync(request, cancellation);

        if (!response.IsSuccessStatusCode)
        {
            return ["tools"];
        }

        using JsonDocument shown = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return shown.RootElement.TryGetProperty("capabilities", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array ? [.. listed.EnumerateArray().Select(capability => capability.GetString()).OfType<string>()] : ["tools"];
    }

    private async Task<Dictionary<string, int>> Loaded(CancellationToken cancellation)
    {
        Dictionary<string, int> loaded = [];

        if (await Get($"{root}/api/ps", cancellation) is not { } running)
        {
            return loaded;
        }

        using (running)
        {
            if (running.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out JsonElement name) && name.GetString() is { } id && model.TryGetProperty("context_length", out JsonElement context) && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out int size))
                    {
                        loaded[id] = size;
                    }
                }
            }
        }

        return loaded;
    }

    private async Task<IReadOnlyList<PromptModel>> Compatible(CancellationToken cancellation)
    {
        if (await Get($"{api}/models", cancellation) is not { } listed)
        {
            return [];
        }

        using (listed)
        {
            if (!listed.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. data.EnumerateArray().Where(entry => entry.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String).Select(entry => entry.GetProperty("id").GetString()!).Select(name => new PromptModel
            {
                Id = $"{ProviderId}/{name}",
                Name = name,
                MaxContextLimit = unknownMaximum,
                Settings = [PromptModelSetting.Count("context", "Context window", 1024, unknownMaximum, preferredContext)]
            })];
        }
    }

    private async Task<JsonDocument?> Get(string url, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await client.GetAsync(url, cancellation);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
