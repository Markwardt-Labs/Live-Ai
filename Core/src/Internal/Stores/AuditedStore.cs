namespace Markwardt.LiveAi;

/// <summary>
/// A view of a store that reports every access to a function.
/// </summary>
/// <param name="store">The store to watch.</param>
/// <param name="sink">Receives each access.</param>
internal sealed class AuditedStore(IStore store, Func<StoreAuditEntry, CancellationToken, Task> sink) : IStore, IStoreTransfer
{
    /// <inheritdoc />
    public async IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        Exception? failure = null;
        IAsyncEnumerator<StoreEntry>? entries = null;

        try
        {
            try
            {
                entries = store.List(directory, recursive, cancellation).GetAsyncEnumerator(cancellation);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }

            while (true)
            {
                bool found;

                try
                {
                    found = await entries.MoveNextAsync();
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                if (!found)
                {
                    break;
                }

                yield return entries.Current;
            }
        }
        finally
        {
            if (entries is not null)
            {
                await entries.DisposeAsync();
            }

            await Report(StoreOperation.List, directory, null, null, failure, CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => store.IsFolder(path, cancellation);

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
        => store.GetSize(path, cancellation);

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
        => Audit(mode == StoreOpenMode.Read ? StoreOperation.Read : StoreOperation.Write, path, null, mode, () => store.Open(path, mode, cancellation), cancellation);

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
        => Audit(StoreOperation.CreateFolder, path, null, null, async () =>
        {
            await store.CreateFolder(path, cancellation);
            return 0;
        }, cancellation);

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
        => Audit(StoreOperation.Delete, path, null, null, async () =>
        {
            await store.Delete(path, cancellation);
            return 0;
        }, cancellation);

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
        => Audit(StoreOperation.Move, from, to, null, async () =>
        {
            await store.Move(from, to, cancellation);
            return 0;
        }, cancellation);

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
        => Audit(StoreOperation.Copy, from, to, null, async () =>
        {
            await store.Copy(from, to, cancellation);
            return 0;
        }, cancellation);

    /// <inheritdoc />
    public async Task<bool> TryMoveTo(string from, IStore target, string to, CancellationToken cancellation = default)
    {
        if (store is not IStoreTransfer transfer)
        {
            return false;
        }

        bool moved = false;
        await Audit(StoreOperation.Move, from, to, null, async () =>
        {
            moved = await transfer.TryMoveTo(from, target, to, cancellation);
            return 0;
        }, cancellation, () => moved);
        return moved;
    }

    private async Task<TResult> Audit<TResult>(StoreOperation operation, string path, string? destination, StoreOpenMode? mode, Func<Task<TResult>> access, CancellationToken cancellation, Func<bool>? happened = null)
    {
        TResult result;

        try
        {
            result = await access();
        }
        catch (Exception exception)
        {
            await Report(operation, path, destination, mode, exception, cancellation);
            throw;
        }

        if (happened?.Invoke() ?? true)
        {
            await Report(operation, path, destination, mode, null, cancellation);
        }

        return result;
    }

    private Task Report(StoreOperation operation, string path, string? destination, StoreOpenMode? mode, Exception? failure, CancellationToken cancellation)
        => sink(new StoreAuditEntry { Time = DateTimeOffset.UtcNow, Operation = operation, Path = path, Destination = destination, Mode = mode, Error = failure?.Message }, cancellation);
}
