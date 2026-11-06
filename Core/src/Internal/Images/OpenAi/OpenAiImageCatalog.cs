namespace Markwardt.LiveAi;

/// <summary>
/// Lists the image models of the OpenAI API, or a server that copies it, and knows what each can do.
/// </summary>
internal interface IOpenAiImageCatalog
{
    /// <summary>
    /// Finds what a model can do beyond a prompt, a size and a quality.
    /// </summary>
    /// <param name="model">The name of the model.</param>
    /// <returns>The features.</returns>
    PromptImageFeatures GetFeatures(string model);

    /// <summary>
    /// Reads the message of an error answer.
    /// </summary>
    /// <param name="text">The body of the answer.</param>
    /// <param name="response">The answer.</param>
    /// <returns>The message the service gave, with the API key left out where it repeats it, or a description of the status.</returns>
    string GetReason(string text, HttpResponseMessage response);

    /// <summary>
    /// Lists the models the service offers that make images, in the order of their names.
    /// </summary>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models.</returns>
    /// <exception cref="InvalidOperationException">The service refused, could not be reached or answered something unreadable.</exception>
    Task<IReadOnlyList<PromptImageModel>> List(CancellationToken cancellation = default);
}

/// <inheritdoc />
/// <param name="apiKey">The API key.</param>
/// <param name="baseUrl">The address of the API with its version path.</param>
/// <param name="http">Sends the requests, and is not disposed by the catalog.</param>
internal sealed class OpenAiImageCatalog(string apiKey, Uri baseUrl, HttpClient http) : IOpenAiImageCatalog
{
    private readonly string endpoint = baseUrl.ToString().TrimEnd('/') + "/models";

    private readonly string[] prefixes = ["gpt-image", "chatgpt-image"];

    /// <inheritdoc />
    public PromptImageFeatures GetFeatures(string model)
        => model.Equals("gpt-image-2", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-image-2-", StringComparison.OrdinalIgnoreCase) ? PromptImageFeatures.None : PromptImageFeatures.Transparency;

    /// <inheritdoc />
    public string GetReason(string text, HttpResponseMessage response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);

            if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message) && message.GetString() is { Length: > 0 } reason)
            {
                return reason.Replace(apiKey, "<key>", StringComparison.Ordinal);
            }
        }
        catch (JsonException)
        {
        }

        return $"The image service answered {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptImageModel>> List(CancellationToken cancellation = default)
    {
        using HttpRequestMessage message = new(HttpMethod.Get, endpoint) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) } };

        try
        {
            using HttpResponseMessage response = await http.SendAsync(message, cancellation);
            string text = await response.Content.ReadAsStringAsync(cancellation);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(GetReason(text, response));
            }

            return Read(text);
        }
        catch (Exception exception) when ((exception is HttpRequestException or TaskCanceledException or JsonException) && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The image service could not be asked for its models: {exception.Message}");
        }
    }

    private List<PromptImageModel> Read(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);

        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The image service answered without a list of models.");
        }

        List<PromptImageModel> models = [];

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } name || !prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            DateTimeOffset? created = item.TryGetProperty("created", out JsonElement seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt64(out long value) ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
            models.Add(new PromptImageModel { Id = name, Features = GetFeatures(name), Created = created });
        }

        models.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return models;
    }
}
