namespace Markwardt.LiveAi;

/// <summary>
/// The files of the workspace with the folders granted for reading: a path that leads into a granted folder reads from it, whether or not the workspace otherwise reaches outside itself, and nothing writes into it, not even through a link or when the whole machine is reachable. Every other path is the workspace's.
/// </summary>
/// <param name="inner">The files of the workspace.</param>
/// <param name="paths">The workspace folder without a sandbox, which tells where a path really leads.</param>
/// <param name="grants">The granted folders.</param>
internal sealed class GrantedStore(IStore inner, DiskStore paths, ReadGrants grants) : IStore, IStoreTransfer
{
    /// <inheritdoc />
    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
        => Granted(directory) is { } shared ? shared.Store.List(shared.Path, recursive, cancellation) : inner.List(directory, recursive, cancellation);

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        if (mode == StoreOpenMode.Read)
        {
            return Granted(path) is { } shared ? shared.Store.Open(shared.Path, mode, cancellation) : inner.Open(path, mode, cancellation);
        }

        Refuse(path);
        return inner.Open(path, mode, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => Granted(path) is { } shared ? shared.Store.IsFolder(shared.Path, cancellation) : inner.IsFolder(path, cancellation);

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
        => Granted(path) is { } shared ? shared.Store.GetSize(shared.Path, cancellation) : inner.GetSize(path, cancellation);

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        Refuse(path);
        return inner.CreateFolder(path, cancellation);
    }

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
    {
        RefuseTaking(path);
        return inner.Delete(path, cancellation);
    }

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
    {
        RefuseTaking(from);
        Refuse(to);
        return inner.Move(from, to, cancellation);
    }

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
    {
        Refuse(to);
        return Granted(from) is { } shared ? CopyOut(shared, from, to, cancellation) : inner.Copy(from, to, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> TryMoveTo(string from, IStore target, string to, CancellationToken cancellation = default)
    {
        RefuseTaking(from);
        return inner is IStoreTransfer transfer ? transfer.TryMoveTo(from, target, to, cancellation) : Task.FromResult(false);
    }

    private (DiskStore Store, string Path, string Full)? Granted(string path)
    {
        if (grants.IsEmpty)
        {
            return null;
        }

        string full = paths.Resolve(path);

        if (grants.Containing(full) is not { } folder)
        {
            return null;
        }

        string relative = Path.GetRelativePath(folder, full);
        // The sandbox of the granted folder refuses the links in it that lead out of it.
        return (new DiskStore(folder, true), relative == "." ? string.Empty : relative, full);
    }

    private void Refuse(string path)
    {
        if (!grants.IsEmpty && grants.Containing(paths.Resolve(path)) is not null)
        {
            throw new UnauthorizedAccessException($"'{path}' is shared for reading only. Copy what you need into the workspace and change the copy.");
        }
    }

    private void RefuseTaking(string path)
    {
        Refuse(path);

        if (!grants.IsEmpty && grants.Holds(paths.Resolve(path)))
        {
            throw new UnauthorizedAccessException($"'{path}' holds a folder that is shared for reading only, so it cannot be moved or deleted.");
        }
    }

    private async Task CopyOut((DiskStore Store, string Path, string Full) shared, string from, string to, CancellationToken cancellation = default)
    {
        bool folder = await shared.Store.IsFolder(shared.Path, cancellation);

        if (!folder)
        {
            await CopyFile(shared.Store, shared.Path, from, to, cancellation);
            return;
        }

        if (await inner.IsFolder(to, cancellation))
        {
            throw new IOException($"'{to}' already exists.");
        }

        await inner.CreateFolder(to, cancellation);

        try
        {
            await CopyContents(shared, from, to, cancellation);
        }
        catch
        {
            try
            {
                await inner.Delete(to, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private async Task CopyContents((DiskStore Store, string Path, string Full) shared, string from, string to, CancellationToken cancellation = default)
    {
        foreach (StoreEntry entry in await shared.Store.List(shared.Path, true, cancellation).ToListAsync(cancellation))
        {
            string target = $"{to.TrimEnd('/')}/{entry.Path}";

            if (entry.IsDirectory)
            {
                await inner.CreateFolder(target, cancellation);
            }
            else
            {
                await CopyFile(shared.Store, shared.Path.Length == 0 ? entry.Path : $"{shared.Path}/{entry.Path}", $"{from.TrimEnd('/')}/{entry.Path}", target, cancellation);
            }
        }
    }

    private async Task CopyFile(DiskStore source, string path, string from, string to, CancellationToken cancellation = default)
    {
        await using (Stream read = await source.Open(path, StoreOpenMode.Read, cancellation))
        {
            await using Stream written = await inner.Open(to, StoreOpenMode.Create, cancellation);
            await read.CopyToAsync(written, cancellation);
        }

        // A copy of a script stays one that can be run.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(paths.Resolve(to), File.GetUnixFileMode(source.Resolve(path)));
        }
    }
}
