namespace Markwardt.LiveAi;

/// <summary>
/// The store the file tools work on: a workspace that relative paths are relative to, with other stores mounted at relative paths inside it. A path that starts with the path of a mount (the longest one that does) is that mount's own store with the path of the mount taken off, every other relative path is in the workspace, and an absolute path is the workspace's own business. Without a workspace only the mounts can be reached.
/// </summary>
/// <param name="workspace">The store that relative paths outside every mount are relative to, or <see langword="null" /> for none.</param>
/// <param name="mounts">The mounted stores by their relative paths, separated by <c>/</c>.</param>
internal sealed class MountedStore(IStore? workspace, IReadOnlyDictionary<string, IStore> mounts) : IStore
{
    private readonly string[] paths = [.. mounts.Keys.OrderByDescending(key => key.Length)];

    /// <inheritdoc />
    public async IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        if (Route(directory) is { Mount: not null } mounted)
        {
            await foreach (StoreEntry entry in mounted.Store.List(mounted.Path, recursive, cancellation))
            {
                yield return entry;
            }

            yield break;
        }

        string? resolved = Absolute(directory) ? null : directory.Resolved();
        string prefix = resolved is { Length: > 0 } ? resolved + "/" : string.Empty;
        string[] below = resolved is null ? [] : [.. paths.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal)];
        HashSet<string> seen = [];
        bool listed = false;

        if (workspace is not null)
        {
            IAsyncEnumerator<StoreEntry>? entries = null;

            try
            {
                entries = workspace.List(resolved ?? directory, recursive, cancellation).GetAsyncEnumerator(cancellation);
                listed = true;
            }
            catch (Exception exception) when (below.Length > 0 && exception is DirectoryNotFoundException)
            {
            }

            if (entries is not null)
            {
                await using (entries)
                {
                    while (true)
                    {
                        try
                        {
                            if (!await entries.MoveNextAsync())
                            {
                                break;
                            }
                        }
                        catch (DirectoryNotFoundException) when (below.Length > 0)
                        {
                            listed = false;
                            break;
                        }

                        string full = prefix + entries.Current.Path;

                        if (!Shadowed(full) && seen.Add(entries.Current.Path))
                        {
                            yield return entries.Current;
                        }
                    }
                }
            }
        }

        if (!listed && below.Length == 0 && !(workspace is null && resolved?.Length == 0))
        {
            throw new DirectoryNotFoundException(workspace is null ? Outside(directory) : $"The folder '{directory}' does not exist.");
        }

        foreach (string key in below)
        {
            string[] parts = key[prefix.Length..].Split('/');

            for (int depth = 1; depth <= (recursive ? parts.Length : 1); depth++)
            {
                string folder = string.Join('/', parts.Take(depth));

                if (seen.Add(folder))
                {
                    yield return new StoreEntry(folder, true);
                }
            }

            if (recursive)
            {
                await foreach (StoreEntry entry in mounts[key].List(string.Empty, true, cancellation))
                {
                    yield return new StoreEntry($"{key[prefix.Length..]}/{entry.Path}", entry.IsDirectory);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsFolder(string path, CancellationToken cancellation = default)
    {
        if (!Absolute(path) && (path.Resolved() ?? throw new ArgumentException($"'{path}' leads outside the store.", nameof(path))).Length == 0)
        {
            return true;
        }

        if (Route(path) is not { } route)
        {
            return false;
        }

        if (route.Mount is not null)
        {
            return route.Path.Length == 0 || await route.Store.IsFolder(route.Path, cancellation);
        }

        return Contains(route.Path) || await route.Store.IsFolder(route.Path, cancellation);
    }

    /// <inheritdoc />
    public async Task<long> GetSize(string path, CancellationToken cancellation = default)
    {
        if (!Absolute(path) && path.Resolved() is { } resolved && Route(path) is not { Mount: not null })
        {
            string prefix = resolved.Length > 0 ? resolved + "/" : string.Empty;
            string[] below = [.. paths.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))];

            if (resolved.Length == 0 || below.Length > 0)
            {
                long total = 0;

                if (workspace is not null && await workspace.IsFolder(resolved, cancellation))
                {
                    total += await workspace.GetSize(resolved, cancellation);
                }

                foreach (string key in below)
                {
                    total += await mounts[key].GetSize(string.Empty, cancellation);
                }

                return total;
            }
        }

        (IStore store, string inner, _) = Route(path) ?? throw new FileNotFoundException(Outside(path));
        return await store.GetSize(inner, cancellation);
    }

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
    {
        if (Route(from) is { } source && Route(to) is { } target && source.Path.Length > 0 && target.Path.Length > 0 && source.Store == target.Store && !Contains(source.Path) && !Contains(target.Path))
        {
            return source.Store.Copy(source.Path, target.Path, cancellation);
        }

        return StoreExtensions.DefaultCopy(this, from, to, cancellation);
    }

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        bool existing = mode is StoreOpenMode.Read or StoreOpenMode.Open;
        (IStore store, string inner, string? mount) = Route(path) ?? throw (existing ? new FileNotFoundException(Outside(path)) : new UnauthorizedAccessException(Outside(path)));
        return !existing && mount is not null && inner.Length == 0 ? Task.FromException<Stream>(new UnauthorizedAccessException($"'{path}' is a mounted folder, not a file.")) : store.Open(inner, mode, cancellation);
    }

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        (IStore store, string inner, string? mount) = Route(path) ?? throw new UnauthorizedAccessException(Outside(path));
        return mount is not null && inner.Length == 0 ? Task.CompletedTask : store.CreateFolder(inner, cancellation);
    }

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
    {
        (IStore store, string inner, string? mount) = Route(path) ?? throw new UnauthorizedAccessException(Outside(path));

        if (mount is not null && inner.Length == 0)
        {
            return Task.FromException(new UnauthorizedAccessException($"'{path}' is a mounted folder and cannot be deleted."));
        }

        return mount is null && Contains(inner) ? Task.FromException(new UnauthorizedAccessException($"'{path}' has a mounted folder in it and cannot be deleted.")) : store.Delete(inner, cancellation);
    }

    /// <inheritdoc />
    public async Task Move(string from, string to, CancellationToken cancellation = default)
    {
        (IStore source, string sourcePath, string? sourceMount) = Route(from) ?? throw new UnauthorizedAccessException(Outside(from));
        (IStore target, string targetPath, string? targetMount) = Route(to) ?? throw new UnauthorizedAccessException(Outside(to));

        if (sourceMount is not null && sourcePath.Length == 0 || targetMount is not null && targetPath.Length == 0 || sourceMount is null && Contains(sourcePath) || targetMount is null && Contains(targetPath))
        {
            throw new UnauthorizedAccessException("A mounted folder, or a folder with one in it, cannot be moved or be the destination of a move.");
        }

        if (source == target)
        {
            await source.Move(sourcePath, targetPath, cancellation);
        }
        else if (source is not IStoreTransfer transfer || !await transfer.TryMoveTo(sourcePath, target, targetPath, cancellation))
        {
            await this.Copy(from, to, cancellation);
            await Delete(from, cancellation);
        }
    }

    private bool Absolute(string path)
        => Path.IsPathRooted(path);

    private bool Shadowed(string resolved)
        => paths.Any(key => resolved == key || resolved.StartsWith(key + "/", StringComparison.Ordinal));

    private bool Contains(string resolved)
        => !Absolute(resolved) && paths.Any(key => resolved.Length == 0 || key.StartsWith(resolved.Trim('/') + "/", StringComparison.Ordinal));

    private (IStore Store, string Path, string? Mount)? Route(string path)
    {
        if (Absolute(path))
        {
            return workspace is null ? null : (workspace, path, null);
        }

        if (path.Resolved() is not { } resolved)
        {
            return workspace is null ? null : (workspace, path, null);
        }

        foreach (string key in paths)
        {
            if (resolved == key)
            {
                return (mounts[key], string.Empty, key);
            }

            if (resolved.StartsWith(key + "/", StringComparison.Ordinal))
            {
                return (mounts[key], resolved[(key.Length + 1)..], key);
            }
        }

        return workspace is null ? null : (workspace, resolved, null);
    }

    private string Outside(string path)
        => workspace is null ? mounts.Count == 0 ? "No folders are mounted." : $"'{path}' is not inside a mounted folder. The mounted folders are: {string.Join(", ", mounts.Keys)}." : $"'{path}' leads outside the workspace.";
}
