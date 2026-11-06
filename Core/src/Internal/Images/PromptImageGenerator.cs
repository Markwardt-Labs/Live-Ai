namespace Markwardt.LiveAi;

/// <summary>
/// Makes images for the AI of a session, which asks for them with the <c>generate_image</c> tool once image generation is set in the configuration (see <see cref="PromptImageOptions" />).
/// </summary>
internal interface IPromptImageGenerator
{
    /// <summary>
    /// Gets what the generator can do beyond the basics, which decides what the AI is offered.
    /// </summary>
    PromptImageFeatures Features { get; }

    /// <summary>
    /// Gets advice on how to describe an image to this generator, which is added to the description of the tool, or <see langword="null" /> for none.
    /// </summary>
    string? PromptGuide { get; }

    /// <summary>
    /// Makes images.
    /// </summary>
    /// <param name="request">What to make.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The images, which can be fewer than asked for when the generator makes fewer. A failure, a refusal included, is thrown, and its message is what the AI reads.</returns>
    /// <exception cref="InvalidOperationException">The generator could not make the image, with the reason.</exception>
    Task<IReadOnlyList<PromptImageResult>> Generate(PromptImageRequest request, CancellationToken cancellation = default);
}
