namespace Markwardt.LiveAi;

/// <summary>
/// Image generation with a Stable Diffusion server on this machine or the network that has the AUTOMATIC1111 API (AUTOMATIC1111 itself or Forge, started with its API on, or another server with that API such as the server of stable-diffusion.cpp). Only how to reach the server is set here: what to draw, the size, the quality, a negative prompt and a seed are for the AI to ask for through its tool.
/// </summary>
public sealed record LocalImageSetup : PromptImageSetup
{
    /// <summary>
    /// Lists the checkpoints of a Stable Diffusion server that can be used for <see cref="Models" />, by the title the server lists them with.
    /// </summary>
    /// <param name="server">The address of the server, or <see langword="null" /> for <c>http://localhost:7860</c>.</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The titles of the checkpoints.</returns>
    /// <exception cref="InvalidOperationException">The server could not be asked for its checkpoints, with the reason (it is not running with its API on, say).</exception>
    public static async Task<IReadOnlyList<string>> ListModels(Uri? server = null, CancellationToken cancellation = default)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
        return [.. (await new StableDiffusionImageCatalog(server ?? new Uri("http://localhost:7860"), http).List(cancellation)).Select(model => model.Id)];
    }

    /// <summary>
    /// Gets the address of the server. It is <c>http://localhost:7860</c> by default.
    /// </summary>
    public Uri Server { get; init; } = new("http://localhost:7860");

    /// <summary>
    /// Gets the checkpoint to load for each capability the AI asks for when it makes an image, by the title the server lists (see <see cref="ListModels" />): one checkpoint for all of them (<see cref="PromptCapabilityModels.All" />) or a different one for each, so the AI can use a fast checkpoint for sketches and a strong one for a final image. It has no default, because which checkpoint draws the images is not something the library can know about a server.
    /// </summary>
    public required PromptCapabilityModels Models { get; init; }
}
