namespace Markwardt.LiveAi;

/// <summary>
/// Lists the models a server on this machine can run (Ollama, LM Studio, llama.cpp, vLLM). Ollama is asked through its own API, which also tells what each model can do, so only the models that can call tools are listed (an embedding model, for one, cannot); any other server is asked through the OpenAI compatible <c>models</c> endpoint, which lists names only, so all of its models are listed.
/// </summary>
/// <param name="baseUrl">The address of the server's OpenAI compatible API, including its version path.</param>
/// <param name="http">The client to ask with, or <see langword="null" /> for one of its own.</param>
public sealed class LocalModelCatalog(string baseUrl = "http://localhost:11434/v1", HttpClient? http = null) : IPromptModelCatalog
{
    private readonly HttpClient client = http ?? new HttpClient();

    private readonly string api = baseUrl.TrimEnd('/');

    private readonly string root = baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? baseUrl.TrimEnd('/')[..^3] : baseUrl.TrimEnd('/');

    /// <summary>
    /// Gets how long the listing waits for the server before it gives an empty list.
    /// </summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

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

                if (name is null || !await CanCallTools(name, entry, cancellation))
                {
                    continue;
                }

                models.Add(new PromptModel { Id = name, ContextLimit = loaded.TryGetValue(name, out int context) ? context : null });
            }

            return models;
        }
    }

    private async Task<bool> CanCallTools(string name, JsonElement entry, CancellationToken cancellation)
    {
        if (entry.TryGetProperty("capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Array)
        {
            return capabilities.EnumerateArray().Any(capability => capability.GetString() == "tools");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}/api/show") { Content = new StringContent(new JsonObject { ["model"] = name }.ToJsonString(), Encoding.UTF8, "application/json") };
        using HttpResponseMessage response = await client.SendAsync(request, cancellation);

        if (!response.IsSuccessStatusCode)
        {
            return true;
        }

        using JsonDocument shown = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        return !shown.RootElement.TryGetProperty("capabilities", out JsonElement listed) || listed.ValueKind != JsonValueKind.Array || listed.EnumerateArray().Any(capability => capability.GetString() == "tools");
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
            return listed.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array ? [.. data.EnumerateArray().Where(entry => entry.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String).Select(entry => new PromptModel { Id = entry.GetProperty("id").GetString()! })] : [];
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
