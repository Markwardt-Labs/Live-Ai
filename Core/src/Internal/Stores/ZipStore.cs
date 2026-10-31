namespace Markwardt.LiveAi;

/// <summary>
/// The files of a zip archive, kept in memory until they are saved.
/// </summary>
/// <param name="path">The full path of the archive file, or <see langword="null" /> when there is none.</param>
internal sealed class ZipStore(string? path) : IZipStore
{
    private readonly MemoryStore files = new();

    private readonly SemaphoreSlim saving = new(1, 1);

    private volatile bool changed;

    string? IZipStore.Path => path;

    /// <inheritdoc />
    public bool HasChanges => changed;

    /// <summary>
    /// Reads the entries of an archive into the store.
    /// </summary>
    /// <param name="source">The archive.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once everything is read.</returns>
    /// <exception cref="InvalidDataException">The archive is damaged, or an entry is named with a path that is absolute or leads outside.</exception>
    public async Task Load(Stream source, CancellationToken cancellation = default)
    {
        using ZipArchive archive = new(source, ZipArchiveMode.Read, true);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            string trimmed = name.TrimEnd('/');

            if (trimmed.Length == 0 || trimmed.Resolved() != trimmed)
            {
                throw new InvalidDataException($"The archive entry '{entry.FullName}' is not a path inside the archive.");
            }

            if (name.EndsWith('/'))
            {
                await files.CreateFolder(trimmed, cancellation);
                continue;
            }

            await using Stream target = await files.Open(trimmed, StoreOpenMode.Replace, cancellation);
            await using Stream data = await entry.OpenAsync(cancellation);
            await data.CopyToAsync(target, cancellation);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
        => files.List(directory, recursive, cancellation);

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        changed |= mode != StoreOpenMode.Read;
        return files.Open(path, mode, cancellation);
    }

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        changed = true;
        return files.CreateFolder(path, cancellation);
    }

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
    {
        changed = true;
        return files.Delete(path, cancellation);
    }

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
    {
        changed = true;
        return files.Move(from, to, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => files.IsFolder(path, cancellation);

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
        => files.GetSize(path, cancellation);

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
    {
        changed = true;
        return files.Copy(from, to, cancellation);
    }

    /// <inheritdoc />
    public async Task Save(CancellationToken cancellation = default)
    {
        string target = path ?? throw new InvalidOperationException("The store was not made from a file, so there is no file to save to. Save it to a stream instead.");
        await saving.WaitAsync(cancellation);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string pending = $"{target}.{Guid.NewGuid():N}.tmp";

            try
            {
                await using (FileStream stream = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    changed = false;
                    await Write(stream, cancellation);
                }

                File.Move(pending, target, true);
            }
            catch
            {
                changed = true;
                File.Delete(pending);
                throw;
            }
        }
        finally
        {
            saving.Release();
        }
    }

    /// <inheritdoc />
    public async Task Save(Stream destination, CancellationToken cancellation = default)
    {
        await saving.WaitAsync(cancellation);

        try
        {
            await Write(destination, cancellation);
        }
        finally
        {
            saving.Release();
        }
    }

    private async Task Write(Stream destination, CancellationToken cancellation = default)
    {
        List<string> paths = await files.ListFiles(string.Empty, true, cancellation).ToListAsync(cancellation);
        List<string> folders = await files.ListFolders(string.Empty, true, cancellation).ToListAsync(cancellation);
        await using ZipArchive archive = new(destination, ZipArchiveMode.Create, true);

        foreach (string file in paths.Order(StringComparer.Ordinal))
        {
            ZipArchiveEntry entry = archive.CreateEntry(file, CompressionLevel.Optimal);
            await using Stream target = await entry.OpenAsync(cancellation);
            await using Stream source = await files.Open(file, StoreOpenMode.Read, cancellation);
            await source.CopyToAsync(target, cancellation);
        }

        foreach (string folder in folders.Where(folder => !paths.Concat(folders).Any(other => other.StartsWith($"{folder}/", StringComparison.Ordinal))).Order(StringComparer.Ordinal))
        {
            archive.CreateEntry($"{folder}/");
        }
    }
}
