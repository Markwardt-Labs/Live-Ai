namespace Markwardt.LiveAi;

/// <summary>
/// Changes kept in memory on top of a store: an upper store that holds what was written, and a set of paths whose content underneath is hidden because it was deleted.
/// </summary>
/// <param name="lower">The store underneath.</param>
internal sealed class OverlayStore(IStore lower) : IOverlayStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);

    private MemoryStore upper = new();

    /// <inheritdoc />
    public async IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        string folder = Clean(directory);
        List<StoreEntry> entries;

        await gate.WaitAsync(cancellation);

        try
        {
            entries = await Entries(folder, recursive, cancellation);
        }
        finally
        {
            gate.Release();
        }

        foreach (StoreEntry entry in entries)
        {
            yield return entry;
        }
    }

    /// <inheritdoc />
    public async Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        string file = Clean(path);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        await gate.WaitAsync(cancellation);

        try
        {
            StoreEntry? found = await Lookup(file, cancellation);

            if (found?.IsDirectory == true)
            {
                throw new IOException($"'{path}' is a folder, not a file.");
            }

            if (mode == StoreOpenMode.Read)
            {
                return found is null ? throw new FileNotFoundException($"The file '{path}' does not exist.") : await (await UpperHas(file, cancellation) ? upper : lower).Open(file, mode, cancellation);
            }

            if (mode == StoreOpenMode.Create && found is not null)
            {
                throw new IOException($"'{path}' already exists.");
            }

            if (mode == StoreOpenMode.Open && found is null)
            {
                throw new FileNotFoundException($"The file '{path}' does not exist.");
            }

            await CheckParents(file, cancellation);

            if (found is not null && mode is StoreOpenMode.Open or StoreOpenMode.Append && !await UpperHas(file, cancellation))
            {
                await CopyUp(file, cancellation);
            }

            return await upper.Open(file, mode, cancellation);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        string folder = Clean(path);

        await gate.WaitAsync(cancellation);

        try
        {
            StoreEntry? found = await Lookup(folder, cancellation);

            if (found?.IsDirectory == true)
            {
                return;
            }

            if (found is not null)
            {
                throw new IOException($"'{path}' is a file, not a folder.");
            }

            await CheckParents(folder, cancellation);
            await upper.CreateFolder(folder, cancellation);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Delete(string path, CancellationToken cancellation = default)
    {
        string target = Clean(path);

        if (target.Length == 0)
        {
            throw new UnauthorizedAccessException("The folder itself cannot be deleted.");
        }

        await gate.WaitAsync(cancellation);

        try
        {
            if (await Lookup(target, cancellation) is null)
            {
                throw new FileNotFoundException($"'{path}' does not exist.");
            }

            if (await UpperHas(target, cancellation))
            {
                await upper.Delete(target, cancellation);
            }

            if (!Hidden(target) && await LowerHas(target, cancellation))
            {
                hidden.RemoveWhere(other => other.StartsWith($"{target}/", StringComparison.Ordinal));
                hidden.Add(target);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Move(string from, string to, CancellationToken cancellation = default)
    {
        string source = Clean(from);
        string target = Clean(to);

        if (source.Length == 0 || target.Length == 0)
        {
            throw new UnauthorizedAccessException("The folder itself cannot be moved or be the destination.");
        }

        if (target.StartsWith($"{source}/", StringComparison.Ordinal))
        {
            throw new IOException($"The folder '{from}' cannot be moved into itself.");
        }

        if (target == source)
        {
            throw new IOException($"'{to}' already exists.");
        }

        await StoreExtensions.DefaultCopy(this, source, target, cancellation);
        await Delete(source, cancellation);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoreChange>> GetChanges(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            List<StoreChange> changes = [];
            List<StoreEntry> made = await upper.List(string.Empty, true, cancellation).ToListAsync(cancellation);

            foreach (StoreEntry entry in made)
            {
                StoreEntry? underneath = await StoreExtensions.Find(lower, entry.Path, cancellation);

                if (entry.IsDirectory)
                {
                    if (underneath is not { IsDirectory: true } || Hidden(entry.Path))
                    {
                        changes.Add(new StoreChange(entry.Path, true, StoreChangeKind.Added));
                    }
                }
                else if (underneath is { IsDirectory: false })
                {
                    if (!await Same(entry.Path, cancellation))
                    {
                        changes.Add(new StoreChange(entry.Path, false, StoreChangeKind.Modified));
                    }
                }
                else
                {
                    changes.Add(new StoreChange(entry.Path, false, StoreChangeKind.Added));
                }
            }

            foreach (string path in hidden)
            {
                if (await StoreExtensions.Find(lower, path, cancellation) is not { } underneath)
                {
                    continue;
                }

                StoreEntry? replacing = made.FirstOrDefault(entry => entry.Path == path);

                if (replacing is null || underneath.IsDirectory || replacing.IsDirectory)
                {
                    changes.Add(new StoreChange(path, underneath.IsDirectory, StoreChangeKind.Deleted));
                }
            }

            return [.. changes.OrderBy(change => change.Path, StringComparer.Ordinal).ThenBy(change => change.Kind)];
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Apply(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            List<StoreEntry> made = await upper.List(string.Empty, true, cancellation).ToListAsync(cancellation);

            foreach (string path in hidden.OrderBy(path => path, StringComparer.Ordinal))
            {
                StoreEntry? replacing = made.FirstOrDefault(entry => entry.Path == path);

                if (await StoreExtensions.Find(lower, path, cancellation) is { } underneath && !(replacing is { IsDirectory: false } && !underneath.IsDirectory))
                {
                    await lower.Delete(path, cancellation);
                }
            }

            foreach (StoreEntry folder in made.Where(entry => entry.IsDirectory).OrderBy(entry => entry.Path.Count(letter => letter == '/')))
            {
                await lower.CreateFolder(folder.Path, cancellation);
            }

            foreach (StoreEntry file in made.Where(entry => !entry.IsDirectory))
            {
                await using Stream source = await upper.Open(file.Path, StoreOpenMode.Read, cancellation);
                await using Stream target = await lower.Open(file.Path, StoreOpenMode.Replace, cancellation);
                await source.CopyToAsync(target, cancellation);
            }

            hidden.Clear();
            upper = new MemoryStore();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Discard(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            hidden.Clear();
            upper = new MemoryStore();
        }
        finally
        {
            gate.Release();
        }
    }

    private string Clean(string path)
        => path.Resolved() ?? throw new ArgumentException($"'{path}' leads outside the store.", nameof(path));

    private bool Hidden(string path)
        => hidden.Any(other => path == other || path.StartsWith($"{other}/", StringComparison.Ordinal));

    private async Task<bool> UpperHas(string path, CancellationToken cancellation)
        => path.Length > 0 && await StoreExtensions.Find(upper, path, cancellation) is not null;

    private async Task<bool> LowerHas(string path, CancellationToken cancellation)
        => path.Length > 0 && await StoreExtensions.Find(lower, path, cancellation) is not null;

    private async Task<StoreEntry?> Lookup(string path, CancellationToken cancellation)
    {
        if (path.Length == 0)
        {
            return new StoreEntry(string.Empty, true);
        }

        if (await StoreExtensions.Find(upper, path, cancellation) is { } made)
        {
            return made;
        }

        return Hidden(path) ? null : await StoreExtensions.Find(lower, path, cancellation);
    }

    private async Task CheckParents(string path, CancellationToken cancellation)
    {
        for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            if (await Lookup(path[..slash], cancellation) is { IsDirectory: false })
            {
                throw new IOException($"'{path[..slash]}' is a file, not a folder.");
            }
        }
    }

    private async Task CopyUp(string path, CancellationToken cancellation)
    {
        await using Stream source = await lower.Open(path, StoreOpenMode.Read, cancellation);
        await using Stream target = await upper.Open(path, StoreOpenMode.Create, cancellation);
        await source.CopyToAsync(target, cancellation);
    }

    private async Task<bool> Same(string path, CancellationToken cancellation)
    {
        await using Stream mine = await upper.Open(path, StoreOpenMode.Read, cancellation);
        await using Stream theirs = await lower.Open(path, StoreOpenMode.Read, cancellation);
        using MemoryStream first = new();
        using MemoryStream second = new();
        await mine.CopyToAsync(first, cancellation);
        await theirs.CopyToAsync(second, cancellation);
        return first.GetBuffer().AsSpan(0, (int)first.Length).SequenceEqual(second.GetBuffer().AsSpan(0, (int)second.Length));
    }

    private async Task<List<StoreEntry>> Entries(string folder, bool recursive, CancellationToken cancellation)
    {
        if (await Lookup(folder, cancellation) is not { IsDirectory: true })
        {
            throw new DirectoryNotFoundException($"The folder '{folder}' does not exist.");
        }

        Dictionary<string, StoreEntry> merged = new(StringComparer.Ordinal);
        string prefix = folder.Length == 0 ? string.Empty : $"{folder}/";

        if (!Hidden(folder) && await StoreExtensions.Find(lower, folder, cancellation) is not { IsDirectory: false })
        {
            try
            {
                await foreach (StoreEntry entry in lower.List(folder, recursive, cancellation))
                {
                    if (!Hidden(prefix + entry.Path))
                    {
                        merged[entry.Path] = entry;
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        if (folder.Length == 0 || await StoreExtensions.Find(upper, folder, cancellation) is { IsDirectory: true })
        {
            await foreach (StoreEntry entry in upper.List(folder, recursive, cancellation))
            {
                merged[entry.Path] = entry;
            }
        }

        return [.. merged.Values];
    }
}
