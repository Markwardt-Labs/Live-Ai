namespace Markwardt.LiveAi;

/// <summary>
/// Makes images with a server that has the AUTOMATIC1111 API.
/// </summary>
/// <param name="baseUrl">The address of the server.</param>
/// <param name="model">The checkpoint to load for each image, or <see langword="null" /> for the one that is loaded.</param>
/// <param name="width">The width when none is asked for.</param>
/// <param name="height">The height when none is asked for.</param>
/// <param name="steps">The sampling steps of a draft, a standard image and a high quality image, in that order.</param>
/// <param name="guidance">The guidance scale.</param>
/// <param name="promptGuide">Advice on how to describe an image, or <see langword="null" /> for the usual advice.</param>
/// <param name="http">Sends the requests, and is not disposed by the generator.</param>
internal sealed class StableDiffusionImageGenerator(Uri baseUrl, string? model, int width, int height, int[] steps, double guidance, string? promptGuide, HttpClient http) : IPromptImageGenerator
{
    private readonly IStableDiffusionImageCatalog catalog = new StableDiffusionImageCatalog(baseUrl, http);

    private readonly string endpoint = baseUrl.ToString().TrimEnd('/') + "/sdapi/v1/txt2img";

    /// <inheritdoc />
    public PromptImageFeatures Features => catalog.GetFeatures();

    /// <inheritdoc />
    public string? PromptGuide { get; } = promptGuide ?? "Describe the image as comma separated phrases (the subject, the setting, the style, the lighting, the camera) rather than long sentences, and name what to avoid in negative_prompt.";

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptImageResult>> Generate(PromptImageRequest request, CancellationToken cancellation = default)
    {
        JsonObject body = new()
        {
            ["prompt"] = request.Prompt,
            ["steps"] = steps[(int)request.Quality],
            ["width"] = Round(request.Width ?? width),
            ["height"] = Round(request.Height ?? height),
            ["cfg_scale"] = guidance,
            ["seed"] = request.Seed ?? -1,
            ["batch_size"] = Math.Clamp(request.Count, 1, 4),
            ["n_iter"] = 1,
            ["send_images"] = true,
            ["save_images"] = false
        };

        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
        {
            body["negative_prompt"] = request.NegativePrompt;
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            body["override_settings"] = new JsonObject { ["sd_model_checkpoint"] = model };
            body["override_settings_restore_afterwards"] = false;
        }

        try
        {
            using StringContent content = new(body.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.PostAsync(endpoint, content, cancellation);
            string text = await response.Content.ReadAsStringAsync(cancellation);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(catalog.GetReason(text, response));
            }

            return Read(text);
        }
        catch (HttpRequestException exception) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The Stable Diffusion server at {baseUrl} could not be reached ({exception.Message}). Start it with its API turned on.");
        }
        catch (Exception exception) when ((exception is TaskCanceledException or JsonException or FormatException) && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The Stable Diffusion server could not be used: {exception.Message}");
        }
    }

    private int Round(int pixels)
        => Math.Clamp((int)Math.Round(pixels / 8.0) * 8, 64, 2048);

    private IReadOnlyList<PromptImageResult> Read(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);

        if (!document.RootElement.TryGetProperty("images", out JsonElement images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("The Stable Diffusion server answered without any image.");
        }

        long[] seeds = [];

        if (document.RootElement.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.String)
        {
            try
            {
                using JsonDocument details = JsonDocument.Parse(info.GetString()!);
                seeds = details.RootElement.TryGetProperty("all_seeds", out JsonElement all) && all.ValueKind == JsonValueKind.Array ? [.. all.EnumerateArray().Where(seed => seed.ValueKind == JsonValueKind.Number).Select(seed => seed.GetInt64())] : [];
            }
            catch (JsonException)
            {
            }
        }

        return [.. images.EnumerateArray().Where(image => image.ValueKind == JsonValueKind.String).Select((image, index) => new PromptImageResult { Content = Convert.FromBase64String(image.GetString()!), Seed = index < seeds.Length ? seeds[index] : null })];
    }
}
