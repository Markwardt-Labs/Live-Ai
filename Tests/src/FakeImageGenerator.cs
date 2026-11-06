namespace Markwardt.LiveAi.Tests;

/// <summary>
/// A generator that makes the images a test gives it and keeps what it was asked for.
/// </summary>
internal sealed class FakeImageGenerator : IPromptImageGenerator
{
    /// <summary>
    /// Gets the bytes that start a PNG file, which is enough to be taken for one.
    /// </summary>
    public static byte[] Png { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    /// <summary>
    /// Gets the bytes that start a JPEG file.
    /// </summary>
    public static byte[] Jpeg { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    /// <summary>
    /// Gets the requests the generator was given, in order.
    /// </summary>
    public List<PromptImageRequest> Requests { get; } = [];

    /// <summary>
    /// Gets or sets what the generator can do.
    /// </summary>
    public PromptImageFeatures Features { get; set; }

    /// <summary>
    /// Gets or sets the advice on describing an image.
    /// </summary>
    public string? PromptGuide { get; set; }

    /// <summary>
    /// Gets or sets the images made for each request, one per count by default.
    /// </summary>
    public Func<PromptImageRequest, IReadOnlyList<PromptImageResult>> Make { get; set; } = request => [.. Enumerable.Range(0, request.Count).Select(index => new PromptImageResult { Content = Png, Seed = 100 + index })];

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptImageResult>> Generate(PromptImageRequest request, CancellationToken cancellation = default)
    {
        Requests.Add(request);
        return Task.FromResult(Make(request));
    }
}
