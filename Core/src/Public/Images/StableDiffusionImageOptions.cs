namespace Markwardt.LiveAi;

/// <summary>
/// Image generation with a Stable Diffusion server on this machine or the network that has the AUTOMATIC1111 API (AUTOMATIC1111 itself or Forge, started with its API on, or another server with that API such as the server of stable-diffusion.cpp). It takes a negative prompt and a seed, and reports the seed. The quality the AI asks for becomes a number of steps, which depends on the model the server loads (a turbo model needs few, a full one many), so give the numbers that suit it.
/// </summary>
public sealed record StableDiffusionImageOptions : PromptImageOptions
{
    /// <summary>
    /// Gets the address of the server. It is <c>http://localhost:7860</c> by default.
    /// </summary>
    public Uri Server { get; init; } = new("http://localhost:7860");

    /// <summary>
    /// Gets the title of the checkpoint to load for each image, as the server lists it, or <see langword="null" /> for the one the server has loaded.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the width in pixels when the AI gives none: what the model was trained at (512 for Stable Diffusion 1.5, 1024 for SDXL).
    /// </summary>
    public int Width { get; init; } = 1024;

    /// <summary>
    /// Gets the height in pixels when the AI gives none.
    /// </summary>
    public int Height { get; init; } = 1024;

    /// <summary>
    /// Gets the sampling steps of a draft.
    /// </summary>
    public int DraftSteps { get; init; } = 8;

    /// <summary>
    /// Gets the sampling steps of a standard image.
    /// </summary>
    public int StandardSteps { get; init; } = 20;

    /// <summary>
    /// Gets the sampling steps of a high quality image.
    /// </summary>
    public int HighSteps { get; init; } = 35;

    /// <summary>
    /// Gets the classifier-free guidance scale (<c>cfg_scale</c>); turbo models want a low one, around 1 to 2.
    /// </summary>
    public double Guidance { get; init; } = 7;

    /// <summary>
    /// Gets advice on how to describe an image to the model, or <see langword="null" /> for the usual advice for Stable Diffusion (comma separated phrases). Give plain sentences for a model that follows them, such as the FLUX family.
    /// </summary>
    public string? PromptGuide { get; init; }
}
