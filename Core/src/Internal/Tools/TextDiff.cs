namespace Markwardt.LiveAi;

/// <summary>
/// Compares two texts line by line.
/// </summary>
internal interface ITextDiff
{
    /// <summary>
    /// Makes the unified diff that turns the first text into the second.
    /// </summary>
    /// <param name="nameA">The name shown for the first text.</param>
    /// <param name="nameB">The name shown for the second text.</param>
    /// <param name="a">The lines of the first text.</param>
    /// <param name="b">The lines of the second text.</param>
    /// <param name="context">How many unchanged lines to show around each change.</param>
    /// <returns>The diff, or an empty string when the texts are the same.</returns>
    /// <exception cref="InvalidOperationException">The texts differ in more lines than a diff is made for.</exception>
    string Unified(string nameA, string nameB, IReadOnlyList<string> a, IReadOnlyList<string> b, int context);
}

/// <inheritdoc />
internal sealed class TextDiff : ITextDiff
{
    private readonly int maxDifferences = 2000;

    /// <inheritdoc />
    public string Unified(string nameA, string nameB, IReadOnlyList<string> a, IReadOnlyList<string> b, int context)
    {
        IReadOnlyList<Edit> edits = Edits(a, b);

        if (edits.All(edit => edit.Kind == EditKind.Same))
        {
            return string.Empty;
        }

        int[] linesA = new int[edits.Count + 1];
        int[] linesB = new int[edits.Count + 1];

        for (int index = 0; index < edits.Count; index++)
        {
            linesA[index + 1] = linesA[index] + (edits[index].Kind == EditKind.Insert ? 0 : 1);
            linesB[index + 1] = linesB[index] + (edits[index].Kind == EditKind.Delete ? 0 : 1);
        }

        StringBuilder text = new();
        text.Append("--- ").Append(nameA).Append('\n').Append("+++ ").Append(nameB).Append('\n');
        int position = 0;

        while (position < edits.Count)
        {
            int first = Next(edits, position);

            if (first < 0)
            {
                break;
            }

            int start = Math.Max(0, first - context);
            int end = first;

            for (int next = Next(edits, end + 1); next >= 0 && next - end - 1 <= 2 * context; next = Next(edits, end + 1))
            {
                end = next;
            }

            end = Math.Min(edits.Count - 1, end + context);
            int countA = linesA[end + 1] - linesA[start];
            int countB = linesB[end + 1] - linesB[start];
            text.Append(CultureInfo.InvariantCulture, $"@@ -{(countA == 0 ? linesA[start] : linesA[start] + 1)},{countA} +{(countB == 0 ? linesB[start] : linesB[start] + 1)},{countB} @@\n");

            for (int index = start; index <= end; index++)
            {
                text.Append(edits[index].Kind switch { EditKind.Delete => '-', EditKind.Insert => '+', _ => ' ' }).Append(edits[index].Line).Append('\n');
            }

            position = end + 1;
        }

        return text.ToString();
    }

    private int Next(IReadOnlyList<Edit> edits, int from)
    {
        for (int index = from; index < edits.Count; index++)
        {
            if (edits[index].Kind != EditKind.Same)
            {
                return index;
            }
        }

        return -1;
    }

    private IReadOnlyList<Edit> Edits(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count;
        int m = b.Count;
        int limit = Math.Min(n + m, maxDifferences);
        int offset = limit + 1;
        int[] v = new int[2 * limit + 3];
        List<int[]> trace = [];

        for (int d = 0; d <= limit; d++)
        {
            trace.Add((int[])v.Clone());

            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]) ? v[k + 1 + offset] : v[k - 1 + offset] + 1;
                int y = x - k;

                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[k + offset] = x;

                if (x >= n && y >= m)
                {
                    return Backtrack(a, b, trace, d, offset);
                }
            }
        }

        throw new InvalidOperationException($"The files differ in more than {maxDifferences} lines, which is too many for a diff.");
    }

    private IReadOnlyList<Edit> Backtrack(IReadOnlyList<string> a, IReadOnlyList<string> b, List<int[]> trace, int depth, int offset)
    {
        List<Edit> edits = [];
        int x = a.Count;
        int y = b.Count;

        for (int d = depth; d > 0; d--)
        {
            int[] v = trace[d];
            int k = x - y;
            int previousK = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]) ? k + 1 : k - 1;
            int previousX = v[previousK + offset];
            int previousY = previousX - previousK;

            while (x > previousX && y > previousY)
            {
                edits.Add(new Edit(EditKind.Same, a[x - 1]));
                x--;
                y--;
            }

            edits.Add(x == previousX ? new Edit(EditKind.Insert, b[y - 1]) : new Edit(EditKind.Delete, a[x - 1]));
            x = previousX;
            y = previousY;
        }

        while (x > 0 && y > 0)
        {
            edits.Add(new Edit(EditKind.Same, a[x - 1]));
            x--;
            y--;
        }

        edits.Reverse();
        return edits;
    }

    private enum EditKind
    {
        Same,
        Delete,
        Insert
    }

    private sealed record Edit(EditKind Kind, string Line);
}
