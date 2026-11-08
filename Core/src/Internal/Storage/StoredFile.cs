namespace Markwardt.LiveAi;

/// <summary>
/// A file of a <see cref="TieredStore" />: its size, when it was used, and where its bytes are, either in memory or in a file of the disk folder. Every member is read and written under the lock of the pool.
/// </summary>
internal sealed class StoredFile
{
    /// <summary>
    /// Gets or sets the bytes of the file while they are in memory, or <see langword="null" /> while they are in a file. The array is never changed once it is set, so a reader can keep it.
    /// </summary>
    public byte[]? Memory { get; set; }

    /// <summary>
    /// Gets or sets the full path of the file that holds the bytes while they are on disk, or <see langword="null" /> while they are in memory.
    /// </summary>
    public string? DiskPath { get; set; }

    /// <summary>
    /// Gets or sets the size of the file in bytes.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Gets or sets the sequence number of the last use, which orders the files by how recently they were used.
    /// </summary>
    public long Used { get; set; }

    /// <summary>
    /// Gets or sets when the file was last used (read or written) on the clock of the session, from which it expires.
    /// </summary>
    public DateTimeOffset UsedAt { get; set; }

    /// <summary>
    /// Gets or sets when the bytes last moved between memory and a file, which keeps a file from moving back at once.
    /// </summary>
    public DateTimeOffset TierAt { get; set; }

    /// <summary>
    /// Gets or sets how many times the file was read from disk since its bytes last moved.
    /// </summary>
    public int Hits { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bytes are being moved between memory and a file.
    /// </summary>
    public bool Moving { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file waits in the queue of files to move back to memory.
    /// </summary>
    public bool Queued { get; set; }
}
