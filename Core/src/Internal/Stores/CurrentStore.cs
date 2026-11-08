namespace Markwardt.LiveAi;

/// <summary>
/// A store that does everything on the store another object gives at that moment, so a caller can keep it while what it stands for is replaced.
/// </summary>
/// <param name="source">Gives the store to work on.</param>
internal sealed class CurrentStore(Func<IStore> source) : IStore
{
    /// <inheritdoc />
    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
        => source().List(directory, recursive, cancellation);

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
        => source().Open(path, mode, cancellation);

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
        => source().CreateFolder(path, cancellation);

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
        => source().Delete(path, cancellation);

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
        => source().Move(from, to, cancellation);

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => source().IsFolder(path, cancellation);

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
        => source().GetSize(path, cancellation);

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
        => source().Copy(from, to, cancellation);
}
