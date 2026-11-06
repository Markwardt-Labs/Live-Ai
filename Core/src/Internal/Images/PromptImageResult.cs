namespace Markwardt.LiveAi;

/// <summary>
/// One image a generator made.
/// </summary>
internal sealed record PromptImageResult
{
    /// <summary>
    /// Gets the encoded image: a PNG, JPEG, GIF or WebP file.
    /// </summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>
    /// Gets the seed the image was made with, or <see langword="null" /> when the generator does not say.
    /// </summary>
    public long? Seed { get; init; }
}
