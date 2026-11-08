namespace Markwardt.LiveAi;

/// <summary>
/// The workspace as the AI's file tools see it, which tells the temporary folder of the workspace about the paths that are opened, created, copied or moved, so that it is made when the AI first uses it and what the AI uses stays, and leaves out of listings the files the library keeps in that folder.
/// </summary>
/// <param name="inner">The workspace.</param>
/// <param name="temp">The temporary folder of the workspace.</param>
internal sealed class WorkspaceTempStore(IStore inner, IWorkspaceTemp temp) : IStore
{
    /// <inheritdoc />
    public async IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        string folder = directory.Resolved() ?? directory;

        await foreach (StoreEntry entry in inner.List(directory, recursive, cancellation))
        {
            if (!temp.IsInternal(folder.Length == 0 ? entry.Path : $"{folder}/{entry.Path}"))
            {
                yield return entry;
            }
        }
    }

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        temp.Touch(path);
        return inner.Open(path, mode, cancellation);
    }

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        temp.Touch(path);
        return inner.CreateFolder(path, cancellation);
    }

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
        => inner.Delete(path, cancellation);

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
    {
        temp.Touch(from);
        temp.Touch(to);
        return inner.Move(from, to, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => inner.IsFolder(path, cancellation);

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
        => inner.GetSize(path, cancellation);

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
    {
        temp.Touch(from);
        temp.Touch(to);
        return inner.Copy(from, to, cancellation);
    }
}
