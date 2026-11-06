namespace Markwardt.LiveAi;

/// <summary>
/// Makes images with the OpenAI image API on the image model that fits a capability, chosen from what the key can use the first time an image is made.
/// </summary>
/// <param name="apiKey">The API key.</param>
/// <param name="capability">How capable the model should be.</param>
/// <param name="handler">Sends the requests, or <see langword="null" /> for the usual one.</param>
internal sealed class OpenAiCapabilityImageGenerator(string apiKey, PromptCapability capability, HttpMessageHandler? handler = null) : IPromptImageGenerator
{
    private readonly SemaphoreSlim resolving = new(1, 1);

    private readonly Uri baseUrl = new("https://api.openai.com/v1");

    private IPromptImageGenerator? inner;

    /// <inheritdoc />
    public PromptImageFeatures Features => PromptImageFeatures.Transparency;

    /// <inheritdoc />
    public string? PromptGuide => null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptImageResult>> Generate(PromptImageRequest request, CancellationToken cancellation = default)
        => await (await Resolve(cancellation)).Generate(request, cancellation);

    private async Task<IPromptImageGenerator> Resolve(CancellationToken cancellation)
    {
        await resolving.WaitAsync(cancellation);

        try
        {
            return inner ??= new OpenAiImageGenerator(apiKey, await Choose(cancellation), baseUrl, new HttpClient(handler ?? new SocketsHttpHandler(), handler is null) { Timeout = TimeSpan.FromMinutes(5) });
        }
        finally
        {
            resolving.Release();
        }
    }

    private async Task<string> Choose(CancellationToken cancellation)
    {
        string hint = capability switch { PromptCapability.Fast => "mini", PromptCapability.Balanced => "flare", _ => "sunburst" };
        string fallback = capability switch { PromptCapability.Fast => "gpt-image-1-mini", PromptCapability.Balanced => "gpt-image-2.6-flare", _ => "gpt-image-2.6-sunburst" };

        try
        {
            using HttpClient http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(30) };
            IReadOnlyList<PromptImageModel> models = await new OpenAiImageCatalog(apiKey, baseUrl, http).List(cancellation);

            return models
                .Where(model => model.Features.HasFlag(PromptImageFeatures.Transparency) && model.Id.Contains(hint, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(model => model.Created ?? DateTimeOffset.MinValue)
                .ThenBy(model => model.Id.Length)
                .Select(model => model.Id)
                .FirstOrDefault() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}
