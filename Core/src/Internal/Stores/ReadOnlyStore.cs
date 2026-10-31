namespace Markwardt.LiveAi;

/// <summary>
/// A view of a store that can be read but not changed.
/// </summary>
/// <param name="store">The store to read.</param>
internal sealed class ReadOnlyStore(IStore store) : IStore, IStoreTransfer
{
    /// <summary>
    /// Gets the store this is a view of.
    /// </summary>
    public IStore Inner => store;

    /// <inheritdoc />
    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
        => store.List(directory, recursive, cancellation);

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => store.IsFolder(path, cancellation);

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
        => store.GetSize(path, cancellation);

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
        => Task.FromException(Refused(to));

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
        => mode == StoreOpenMode.Read ? store.Open(path, mode, cancellation) : Task.FromException<Stream>(Refused(path));

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
        => Task.FromException(Refused(path));

    /// <inheritdoc />
    public Task<bool> TryMoveTo(string from, IStore target, string to, CancellationToken cancellation = default)
        => Task.FromException<bool>(Refused(from));

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
        => Task.FromException(Refused(path));

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
        => Task.FromException(Refused(from));

    private UnauthorizedAccessException Refused(string path)
        => new($"'{path}' is read-only: it cannot be changed.");
}
