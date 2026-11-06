namespace Markwardt.LiveAi;

/// <summary>
/// Reads the lines of a file from the last to the first without reading the whole file first.
/// </summary>
internal sealed class ReverseLineReader
{
    private readonly int chunk = 64 * 1024;

    /// <summary>
    /// Reads the lines of a stream from the end, as the stream is when this is called: lines written later are not seen.
    /// </summary>
    /// <param name="stream">The stream, which must be seekable and is not disposed.</param>
    /// <param name="cancellation">Stops the reading.</param>
    /// <returns>Each non-empty line with the offset in bytes at which it starts, the last line first.</returns>
    public async IAsyncEnumerable<(long Offset, string Line)> Read(Stream stream, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        long position = stream.Length;
        byte[] tail = [];

        while (position > 0)
        {
            int size = (int)Math.Min(chunk, position);
            position -= size;
            byte[] combined = new byte[size + tail.Length];
            stream.Seek(position, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(combined.AsMemory(0, size), cancellation);
            tail.CopyTo(combined, size);

            int end = combined.Length;

            for (int index = combined.Length - 1; index >= 0; index--)
            {
                if (combined[index] != (byte)'\n')
                {
                    continue;
                }

                if (end > index + 1 && Decode(combined.AsSpan(index + 1, end - index - 1)) is { Length: > 0 } line)
                {
                    yield return (position + index + 1, line);
                }

                end = index;
            }

            tail = combined[..end];
        }

        if (Decode(tail) is { Length: > 0 } first)
        {
            yield return (0, first);
        }
    }

    private string Decode(ReadOnlySpan<byte> bytes)
        => Encoding.UTF8.GetString(bytes).TrimEnd('\r');
}
