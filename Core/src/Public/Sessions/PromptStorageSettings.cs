namespace Markwardt.LiveAi;

/// <summary>
/// How the session's own storage behaves: the session storage (<c>.liveai-session</c>, which lasts the session) and the cache (<c>.liveai-cache</c>, which is cleared after a while), named by <see cref="PromptStorageNames" />. Both keep what is written to them in memory and move what does not fit to files in the data folder, in the background, where neither the AI nor a caller can tell. There is no limit on the total, only on a single file and on how much is held in memory.
/// </summary>
public sealed record PromptStorageSettings
{
    /// <summary>
    /// Gets how many bytes the session storage and the cache hold in memory together before the files that were used least recently are moved to files. It is a target, not a ceiling: when a file cannot be moved (the disk is not writable) it stays in memory. Zero keeps everything in files, and <see cref="long.MaxValue" /> never uses files, which leaves the memory they use unbounded. It is 32 MiB by default, and applies to each session.
    /// </summary>
    public long MemoryLimit { get; init; } = 32L * 1024 * 1024;

    /// <summary>
    /// Gets the most bytes a single file may have in the session storage or the cache, and the most a single download may have, wherever it is saved. A larger write fails and says so. It is 256 MiB by default.
    /// </summary>
    public long FileLimit { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets how long a file in the cache or in the <c>.liveai-temp</c> folder of the workspace is kept after it was last used (read or written) before it is removed, which is 30 minutes by default. Running a command counts as a use of everything in the <c>.liveai-temp</c> folder, since what a command reads cannot be seen.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Checks that the settings can be used.
    /// </summary>
    /// <exception cref="ArgumentException">A limit is negative, the file limit is not positive, or the retention is not positive.</exception>
    internal void Check()
    {
        if (MemoryLimit < 0)
        {
            throw new ArgumentException("The memory limit cannot be negative.", nameof(MemoryLimit));
        }

        if (FileLimit < 1)
        {
            throw new ArgumentException("The file limit must be at least one byte.", nameof(FileLimit));
        }

        if (Retention <= TimeSpan.Zero)
        {
            throw new ArgumentException("The retention must be longer than nothing.", nameof(Retention));
        }
    }
}
