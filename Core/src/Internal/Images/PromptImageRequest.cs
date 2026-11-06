namespace Markwardt.LiveAi;

/// <summary>
/// What a generator is asked to make.
/// </summary>
internal sealed record PromptImageRequest
{
    /// <summary>
    /// Gets the description of the image.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Gets the width in pixels, or <see langword="null" /> for the generator's own. A generator rounds it to what it can make.
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    /// Gets the height in pixels, or <see langword="null" /> for the generator's own. A generator rounds it to what it can make.
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    /// Gets how many images to make, from 1 to 4.
    /// </summary>
    public int Count { get; init; } = 1;

    /// <summary>
    /// Gets how much effort to put into each image.
    /// </summary>
    public PromptImageQuality Quality { get; init; } = PromptImageQuality.Standard;

    /// <summary>
    /// Gets a value indicating whether the background is to be transparent. Only a generator with <see cref="PromptImageFeatures.Transparency" /> is asked this.
    /// </summary>
    public bool Transparent { get; init; }

    /// <summary>
    /// Gets what the image is to leave out, or <see langword="null" /> for nothing. Only a generator with <see cref="PromptImageFeatures.NegativePrompt" /> is given this.
    /// </summary>
    public string? NegativePrompt { get; init; }

    /// <summary>
    /// Gets the seed that makes the image repeatable, or <see langword="null" /> for a random one. Only a generator with <see cref="PromptImageFeatures.Seed" /> is given this.
    /// </summary>
    public long? Seed { get; init; }

    /// <summary>
    /// Gets how capable the model that makes the image should be, or <see langword="null" /> for the generator's usual one. Only a generator with <see cref="PromptImageFeatures.Capability" /> takes it.
    /// </summary>
    public PromptCapability? Capability { get; init; }
}
