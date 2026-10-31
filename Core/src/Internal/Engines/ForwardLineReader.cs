namespace Markwardt.LiveAi;

/// <summary>
/// Reads the lines of a file from the first to the last, with the offset in bytes at which each starts, which is the position that <see cref="ReverseLineReader" /> reports for the same line.
/// </summary>
internal sealed class ForwardLineReader
{
    /// <summary>
    /// Reads all the non-empty lines of a file, as it is when this is called.
    /// </summary>
    /// <param name="path">The file, which may be written to by another process.</param>
    /// <param name="cancellation">Stops the reading.</param>
    /// <returns>Each non-empty line with the offset in bytes at which it starts, the first line first.</returns>
    public async Task<IReadOnlyList<(long Offset, string Line)>> Read(string path, CancellationToken cancellation = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellation);
        byte[] bytes = buffer.ToArray();
        List<(long Offset, string Line)> lines = [];

        for (int start = 0; start < bytes.Length;)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', start);
            int length = (end < 0 ? bytes.Length : end) - start;
            string line = Encoding.UTF8.GetString(bytes, start, length).TrimEnd('\r');

            if (line.Length > 0)
            {
                lines.Add((start, line));
            }

            start = end < 0 ? bytes.Length : end + 1;
        }

        return lines;
    }
}
