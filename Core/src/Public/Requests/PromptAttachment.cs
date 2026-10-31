namespace Markwardt.LiveAi;

/// <summary>
/// A file that goes along with a prompt (see <see cref="PromptOptions.Attachments" />). Two attachments are equal when their name, media type and content are. Images (PNG, JPEG, GIF and WebP) are shown to the AI, and so are PDF documents with Claude Code; text files (such as <c>text/plain</c>, <c>text/csv</c> or <c>application/json</c>) are put into the prompt as text, with both providers. Anything else is refused when the prompt is sent, because there is no way to give it to the AI directly: mount it instead and let the AI read it with the file tools.
/// </summary>
public sealed record PromptAttachment
{
    /// <summary>
    /// Reads a file from disk, taking the media type from its extension (<c>application/octet-stream</c> when the extension is not a known one).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The attachment.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static async Task<PromptAttachment> FromFile(string path, CancellationToken cancellation = default)
        => new()
        {
            Name = Path.GetFileName(path),
            MediaType = path.ToMediaType(),
            Content = await File.ReadAllBytesAsync(path, cancellation)
        };

    /// <summary>
    /// Gets the name of the file, which the AI is told for a text file.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the media type, such as <c>image/png</c>, <c>application/pdf</c> or <c>text/plain</c>.
    /// </summary>
    public required string MediaType { get; init; }

    /// <summary>
    /// Gets the bytes of the file.
    /// </summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <inheritdoc />
    public bool Equals(PromptAttachment? other)
        => other is not null && Name == other.Name && MediaType == other.MediaType && Content.Span.SequenceEqual(other.Content.Span);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Name, MediaType, Content.Length);
}
