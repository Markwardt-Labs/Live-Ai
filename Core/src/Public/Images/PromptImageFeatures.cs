namespace Markwardt.LiveAi;

/// <summary>
/// What a generator can do beyond a prompt, a size and a quality, which decides what the AI is offered when it asks for an image.
/// </summary>
[Flags]
public enum PromptImageFeatures
{
    /// <summary>
    /// Nothing beyond the basics.
    /// </summary>
    None = 0,

    /// <summary>
    /// It can make an image with a transparent background (<see cref="PromptImageRequest.Transparent" />).
    /// </summary>
    Transparency = 1,

    /// <summary>
    /// It takes a description of what to leave out (<see cref="PromptImageRequest.NegativePrompt" />).
    /// </summary>
    NegativePrompt = 2,

    /// <summary>
    /// It takes a seed that makes an image repeatable (<see cref="PromptImageRequest.Seed" />) and reports the seed it used.
    /// </summary>
    Seed = 4
}
