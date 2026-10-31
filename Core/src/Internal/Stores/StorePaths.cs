namespace Markwardt.LiveAi;

/// <summary>
/// The one way a path into a <see cref="IStore" /> is cleaned, so that two spellings of the same place are always the same path.
/// </summary>
internal static class StorePaths
{
    extension(string path)
    {
        /// <summary>
        /// Resolves redundant slashes and <c>.</c> and <c>..</c> segments of a path that uses <c>/</c>.
        /// </summary>
        /// <returns>The path in its simplest form, which is empty for the top; <see langword="null" /> when it climbs above the top.</returns>
        public string? Resolved()
        {
            List<string> segments = [];

            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == "..")
                {
                    if (segments.Count == 0)
                    {
                        return null;
                    }

                    segments.RemoveAt(segments.Count - 1);
                }
                else if (segment != ".")
                {
                    segments.Add(segment);
                }
            }

            return string.Join('/', segments);
        }
    }
}
