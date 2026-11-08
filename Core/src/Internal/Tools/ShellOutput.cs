namespace Markwardt.LiveAi;

/// <summary>
/// The output of a shell command as it is written, kept up to a limit.
/// </summary>
/// <param name="limit">The most characters kept; later lines are dropped.</param>
internal sealed class ShellOutput(int limit)
{
    private readonly List<string> lines = [];

    private readonly object gate = new();

    private int length;

    /// <summary>
    /// Gets a value indicating whether lines were dropped because the output reached its limit.
    /// </summary>
    public bool IsCut { get; private set; }

    /// <summary>
    /// Adds a line of output, or drops it when the limit is reached.
    /// </summary>
    /// <param name="line">The line.</param>
    public void Add(string line)
    {
        lock (gate)
        {
            if (length >= limit)
            {
                IsCut = true;
                return;
            }

            lines.Add(line);
            length += line.Length + 1;
        }
    }

    /// <summary>
    /// Gets the last lines of the output.
    /// </summary>
    /// <param name="count">The most lines to return.</param>
    /// <returns>The lines joined by line breaks, without a trailing one.</returns>
    public string Tail(int count)
    {
        lock (gate)
        {
            return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - count)));
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        lock (gate)
        {
            return string.Concat(lines.Select(line => line + Environment.NewLine));
        }
    }
}
