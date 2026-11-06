namespace Markwardt.LiveAi;

/// <summary>
/// Lists the checkpoints of a server that has the AUTOMATIC1111 API, and knows what its generator can do.
/// </summary>
internal interface IStableDiffusionImageCatalog
{
    /// <summary>
    /// Finds what the generator can do beyond a prompt, a size and a quality, which is the same for every checkpoint.
    /// </summary>
    /// <returns>The features.</returns>
    PromptImageFeatures GetFeatures();

    /// <summary>
    /// Reads the message of an error answer.
    /// </summary>
    /// <param name="text">The body of the answer.</param>
    /// <param name="response">The answer.</param>
    /// <returns>The message the server gave, or a description of the status.</returns>
    string GetReason(string text, HttpResponseMessage response);

    /// <summary>
    /// Lists the checkpoints the server has, in the order of their names.
    /// </summary>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models.</returns>
    /// <exception cref="InvalidOperationException">The server refused, could not be reached or answered something unreadable.</exception>
    Task<IReadOnlyList<PromptImageModel>> List(CancellationToken cancellation = default);
}

/// <inheritdoc />
/// <param name="baseUrl">The address of the server.</param>
/// <param name="http">Sends the requests, and is not disposed by the catalog.</param>
internal sealed class StableDiffusionImageCatalog(Uri baseUrl, HttpClient http) : IStableDiffusionImageCatalog
{
    private readonly string endpoint = baseUrl.ToString().TrimEnd('/') + "/sdapi/v1/sd-models";

    /// <inheritdoc />
    public PromptImageFeatures GetFeatures()
        => PromptImageFeatures.NegativePrompt | PromptImageFeatures.Seed;

    /// <inheritdoc />
    public string GetReason(string text, HttpResponseMessage response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);

            foreach (string name in (string[])["error", "detail", "message"])
            {
                if (document.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } reason)
                {
                    return reason;
                }
            }
        }
        catch (JsonException)
        {
        }

        return $"The Stable Diffusion server answered {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptImageModel>> List(CancellationToken cancellation = default)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(endpoint, cancellation);
            string text = await response.Content.ReadAsStringAsync(cancellation);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(GetReason(text, response));
            }

            return Read(text);
        }
        catch (HttpRequestException exception) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The Stable Diffusion server at {baseUrl} could not be reached ({exception.Message}). Start it with its API turned on.");
        }
        catch (Exception exception) when ((exception is TaskCanceledException or JsonException) && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The Stable Diffusion server could not be asked for its models: {exception.Message}");
        }
    }

    private List<PromptImageModel> Read(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The Stable Diffusion server answered without a list of models.");
        }

        List<PromptImageModel> models = [];

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? title = Text(item, "title");
            string? name = Text(item, "model_name");
            string? id = title ?? name ?? Text(item, "filename");

            if (id is not null)
            {
                models.Add(new PromptImageModel { Id = id, Name = name is not null && name != id ? name : null, Features = GetFeatures() });
            }
        }

        models.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return models;
    }

    private string? Text(JsonElement item, string property)
        => item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
