namespace Markwardt.LiveAi;

/// <summary>
/// Makes images with the OpenAI image API on the image model that fits the capability each request asks for (the cheapest for <see cref="PromptCapability.Fast" />, the everyday one for <see cref="PromptCapability.Balanced" />, and the most capable for <see cref="PromptCapability.Strong" /> and <see cref="PromptCapability.Max" />, since OpenAI has no tier between), chosen from the image models the key can use the first time a capability is asked for.
/// </summary>
/// <param name="apiKey">The API key.</param>
/// <param name="handler">Sends the requests, or <see langword="null" /> for the usual one.</param>
internal sealed class OpenAiCapabilityImageGenerator(string apiKey, HttpMessageHandler? handler = null) : IPromptImageGenerator
{
    private readonly SemaphoreSlim resolving = new(1, 1);

    private readonly Uri baseUrl = new("https://api.openai.com/v1");

    private readonly Dictionary<PromptCapability, IPromptImageGenerator> made = [];

    private IReadOnlyList<PromptImageModel>? listed;

    /// <inheritdoc />
    public PromptImageFeatures Features => PromptImageFeatures.Transparency | PromptImageFeatures.Capability;

    /// <inheritdoc />
    public string? PromptGuide => null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptImageResult>> Generate(PromptImageRequest request, CancellationToken cancellation = default)
        => await (await Resolve(request.Capability ?? PromptCapability.Balanced, cancellation)).Generate(request, cancellation);

    private async Task<IPromptImageGenerator> Resolve(PromptCapability capability, CancellationToken cancellation)
    {
        await resolving.WaitAsync(cancellation);

        try
        {
            if (!made.TryGetValue(capability, out IPromptImageGenerator? generator))
            {
                made[capability] = generator = new OpenAiImageGenerator(apiKey, await Choose(capability, cancellation), baseUrl, new HttpClient(handler ?? new SocketsHttpHandler(), handler is null) { Timeout = TimeSpan.FromMinutes(5) });
            }

            return generator;
        }
        finally
        {
            resolving.Release();
        }
    }

    private async Task<string> Choose(PromptCapability capability, CancellationToken cancellation)
    {
        string hint = capability switch { PromptCapability.Fast => "mini", PromptCapability.Balanced => "flare", _ => "sunburst" };
        string fallback = capability switch { PromptCapability.Fast => "gpt-image-1-mini", PromptCapability.Balanced => "gpt-image-2.6-flare", _ => "gpt-image-2.6-sunburst" };

        return (await Models(cancellation))
            .Where(model => model.Features.HasFlag(PromptImageFeatures.Transparency) && model.Id.Contains(hint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(model => model.Created ?? DateTimeOffset.MinValue)
            .ThenBy(model => model.Id.Length)
            .Select(model => model.Id)
            .FirstOrDefault() ?? fallback;
    }

    private async Task<IReadOnlyList<PromptImageModel>> Models(CancellationToken cancellation)
    {
        if (listed is not null)
        {
            return listed;
        }

        try
        {
            using HttpClient http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(30) };
            return listed = await new OpenAiImageCatalog(apiKey, baseUrl, http).List(cancellation);
        }
        catch (InvalidOperationException)
        {
            return listed = [];
        }
    }
}
