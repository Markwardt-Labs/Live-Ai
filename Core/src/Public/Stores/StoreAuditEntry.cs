namespace Markwardt.LiveAi;

/// <summary>
/// One access to an audited store, as reported to the function given to <see cref="StoreExtensions.Audited(IStore, Func{StoreAuditEntry, CancellationToken, Task})" />.
/// </summary>
public sealed record StoreAuditEntry
{
    /// <summary>
    /// Gets when the access finished (for a listing, when it was read to its end or given up).
    /// </summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>
    /// Gets what was done.
    /// </summary>
    public required StoreOperation Operation { get; init; }

    /// <summary>
    /// Gets the path that was accessed, as the store was asked for it.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets where a move or copy went, or <see langword="null" /> for the other operations.
    /// </summary>
    public string? Destination { get; init; }

    /// <summary>
    /// Gets how the file was opened for <see cref="StoreOperation.Read" /> and <see cref="StoreOperation.Write" />, and <see langword="null" /> for the other operations.
    /// </summary>
    public StoreOpenMode? Mode { get; init; }

    /// <summary>
    /// Gets the message of the error that made the access fail, or <see langword="null" /> when it worked.
    /// </summary>
    public string? Error { get; init; }
}
