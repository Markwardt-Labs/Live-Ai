namespace Markwardt.LiveAi;

/// <summary>
/// Sizes and durations in words, for what the AI and the caller are told.
/// </summary>
internal static class Quantities
{
    extension(long bytes)
    {
        /// <summary>
        /// Writes a number of bytes the way a person reads it.
        /// </summary>
        /// <returns>The text, in MiB, KiB or bytes by size.</returns>
        public string DescribeSize()
            => bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024} MiB" : bytes >= 1024 ? $"{bytes / 1024} KiB" : $"{bytes} bytes";
    }

    extension(TimeSpan time)
    {
        /// <summary>
        /// Writes a duration the way a person reads it.
        /// </summary>
        /// <returns>The text, in minutes where it is a whole number of them and in seconds otherwise.</returns>
        public string DescribeDuration()
            => time.Ticks % TimeSpan.TicksPerMinute == 0 ? (time.TotalMinutes == 1 ? "1 minute" : $"{time.TotalMinutes:0} minutes") : $"{Math.Max(1, (long)time.TotalSeconds)} seconds";
    }
}
