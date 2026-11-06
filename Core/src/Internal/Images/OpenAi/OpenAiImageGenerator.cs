namespace Markwardt.LiveAi;

/// <summary>
/// Makes images with the OpenAI image API, or a server that copies it.
/// </summary>
/// <param name="apiKey">The API key.</param>
/// <param name="model">The model to use.</param>
/// <param name="baseUrl">The address of the API with its version path.</param>
/// <param name="http">Sends the requests, and is not disposed by the generator.</param>
internal sealed class OpenAiImageGenerator(string apiKey, string model, Uri baseUrl, HttpClient http) : IPromptImageGenerator
{
    private readonly IOpenAiImageCatalog catalog = new OpenAiImageCatalog(apiKey, baseUrl, http);

    private readonly string endpoint = baseUrl.ToString().TrimEnd('/') + "/images/generations";

    /// <inheritdoc />
    public PromptImageFeatures Features => catalog.GetFeatures(model);

    /// <inheritdoc />
    public string? PromptGuide => null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptImageResult>> Generate(PromptImageRequest request, CancellationToken cancellation = default)
    {
        JsonObject body = new()
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["n"] = Math.Clamp(request.Count, 1, 4),
            ["size"] = Size(request.Width, request.Height),
            ["quality"] = request.Quality switch { PromptImageQuality.Draft => "low", PromptImageQuality.High => "high", _ => "medium" }
        };

        if (request.Transparent)
        {
            body["background"] = "transparent";
            body["output_format"] = "png";
        }

        using HttpRequestMessage message = new(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) } };

        try
        {
            using HttpResponseMessage response = await http.SendAsync(message, cancellation);
            string text = await response.Content.ReadAsStringAsync(cancellation);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(catalog.GetReason(text, response));
            }

            return Read(text);
        }
        catch (Exception exception) when ((exception is HttpRequestException or TaskCanceledException or JsonException or FormatException) && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The image service could not be used: {exception.Message}");
        }
    }

    private string Size(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0)
        {
            return "auto";
        }

        double ratio = (double)width.Value / height.Value;
        return ratio > 1.2 ? "1536x1024" : ratio < 0.83 ? "1024x1536" : "1024x1024";
    }

    private IReadOnlyList<PromptImageResult> Read(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        List<PromptImageResult> results = [];

        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The image service answered without any image.");
        }

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out JsonElement encoded) && encoded.ValueKind == JsonValueKind.String)
            {
                results.Add(new PromptImageResult { Content = Convert.FromBase64String(encoded.GetString()!) });
            }
        }

        return results.Count > 0 ? results : throw new InvalidOperationException("The image service answered without any image.");
    }
}
