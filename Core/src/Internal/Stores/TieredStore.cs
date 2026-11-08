namespace Markwardt.LiveAi;

/// <summary>
/// Files and folders that are kept in memory and move to files on disk, and back, as the pool of the session needs the memory. Folders are real: one can be empty, moved and deleted. A file that was not used for the retention can expire. Moving between memory and disk is not visible: a reader gets the same bytes either way.
/// </summary>
/// <param name="pool">The pool this store shares its memory and its disk folder with.</param>
/// <param name="name">The name of the store, which is the name of its folder on disk.</param>
/// <param name="expires">Whether files that were not used for the retention are removed.</param>
internal sealed class TieredStore(IStoragePool pool, string name, bool expires) : IStore, IStoreTransfer
{
    private readonly Dictionary<string, StoredFile> files = new(StringComparer.Ordinal);

    private readonly Dictionary<string, DateTimeOffset> folders = new(StringComparer.Ordinal) { [string.Empty] = DateTimeOffset.MaxValue };

    /// <summary>
    /// Gets the name of the store.
    /// </summary>
    public string Name => name;

    /// <summary>
    /// Gets a value indicating whether files that were not used for the retention are removed.
    /// </summary>
    public bool Expires => expires;

    /// <summary>
    /// Gets the pool of the store.
    /// </summary>
    internal IStoragePool PoolOf => pool;

    /// <inheritdoc />
    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
    {
        lock (pool.Gate)
        {
            if (!folders.ContainsKey(directory))
            {
                throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");
            }

            string prefix = directory.Length == 0 ? string.Empty : $"{directory}/";
            bool Inside(string path)
                => recursive ? path.StartsWith(prefix, StringComparison.Ordinal) : Parent(path) == directory;

            return files.Keys.Where(Inside).Select(path => new StoreEntry(path[prefix.Length..], false)).Concat(folders.Keys.Where(path => path.Length > 0 && Inside(path)).Select(path => new StoreEntry(path[prefix.Length..], true))).ToList().ToAsyncEnumerable();
        }
    }

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
    {
        lock (pool.Gate)
        {
            return Task.FromResult(folders.ContainsKey(path));
        }
    }

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
    {
        lock (pool.Gate)
        {
            if (files.TryGetValue(path, out StoredFile? file))
            {
                return Task.FromResult(file.Length);
            }

            if (!folders.ContainsKey(path))
            {
                throw new FileNotFoundException($"'{path}' does not exist.");
            }

            string prefix = path.Length == 0 ? string.Empty : $"{path}/";
            return Task.FromResult(files.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(entry => entry.Value.Length));
        }
    }

    /// <inheritdoc />
    public async Task Copy(string from, string to, CancellationToken cancellation = default)
    {
        List<(string Path, StoredFile Source)> copies = [];
        List<string> newFolders = [];

        lock (pool.Gate)
        {
            if (from.Length == 0 || to.Length == 0)
            {
                throw new ArgumentException("The root of the store itself cannot be copied or be the destination.");
            }

            bool file = files.ContainsKey(from);

            if (!file && !folders.ContainsKey(from))
            {
                throw new FileNotFoundException($"'{from}' does not exist.");
            }

            if (files.ContainsKey(to) || folders.ContainsKey(to))
            {
                throw new IOException($"'{to}' already exists.");
            }

            if (!file && to.StartsWith($"{from}/", StringComparison.Ordinal))
            {
                throw new IOException($"The folder '{from}' cannot be copied into itself.");
            }

            Missing(to);

            if (file)
            {
                copies.Add((to, files[from]));
            }
            else
            {
                copies.AddRange(files.Where(entry => entry.Key.StartsWith($"{from}/", StringComparison.Ordinal)).Select(entry => ($"{to}{entry.Key[from.Length..]}", entry.Value)));
                newFolders.AddRange(folders.Keys.Where(path => path == from || path.StartsWith($"{from}/", StringComparison.Ordinal)).Select(path => $"{to}{path[from.Length..]}"));
            }

            foreach ((_, StoredFile source) in copies)
            {
                Use(source);
            }
        }

        List<StoredFile> made = [];

        try
        {
            foreach ((_, StoredFile source) in copies)
            {
                made.Add(await Duplicate(source, cancellation));
            }
        }
        catch
        {
            Erase(made.Select(copy => copy.DiskPath).OfType<string>());
            throw;
        }

        List<string> garbage = [];
        bool taken;

        lock (pool.Gate)
        {
            taken = files.ContainsKey(to) || folders.ContainsKey(to);

            if (taken)
            {
                garbage.AddRange(made.Select(copy => copy.DiskPath).OfType<string>());
            }
            else
            {
                for (int index = 0; index < copies.Count; index++)
                {
                    Install(copies[index].Path, made[index], garbage);
                }

                DateTimeOffset now = pool.Time.GetUtcNow();

                foreach (string folder in newFolders)
                {
                    folders[folder] = now;
                }

                MakeParents(to);
            }
        }

        Erase(garbage);

        if (taken)
        {
            throw new IOException($"'{to}' already exists.");
        }
    }

    /// <inheritdoc />
    public async Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        return mode == StoreOpenMode.Read ? await OpenRead(path) : await OpenWrite(path, mode, cancellation);
    }

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
    {
        List<string> garbage = [];

        lock (pool.Gate)
        {
            if (path.Length == 0)
            {
                throw new UnauthorizedAccessException("The folder itself cannot be deleted.");
            }

            if (files.TryGetValue(path, out StoredFile? file))
            {
                Release(file, garbage);
                files.Remove(path);
            }
            else if (folders.ContainsKey(path))
            {
                foreach (string inside in files.Keys.Where(other => other.StartsWith($"{path}/", StringComparison.Ordinal)).ToList())
                {
                    Release(files[inside], garbage);
                    files.Remove(inside);
                }

                foreach (string inside in folders.Keys.Where(other => other == path || other.StartsWith($"{path}/", StringComparison.Ordinal)).ToList())
                {
                    folders.Remove(inside);
                }
            }
            else
            {
                throw new FileNotFoundException($"'{path}' does not exist.");
            }
        }

        Erase(garbage);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        lock (pool.Gate)
        {
            if (files.ContainsKey(path))
            {
                throw new IOException($"'{path}' is a file, not a folder.");
            }

            if (path.Length > 0)
            {
                Missing(path);
                MakeParents(path);
                folders[path] = pool.Time.GetUtcNow();
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
    {
        lock (pool.Gate)
        {
            Relocate(from, this, to);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<bool> TryMoveTo(string from, IStore target, string to, CancellationToken cancellation = default)
    {
        if (target is not TieredStore other || other == this || !ReferenceEquals(other.PoolOf, pool))
        {
            return Task.FromResult(false);
        }

        lock (pool.Gate)
        {
            Relocate(from, other, to);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Finds the file in memory that was used least recently, and that is not being moved. The caller holds the lock of the pool.
    /// </summary>
    /// <returns>The file, or <see langword="null" /> when no file can move.</returns>
    internal StoredFile? Coldest()
    {
        StoredFile? coldest = null;

        foreach (StoredFile file in files.Values)
        {
            if (file is { Memory: not null, Moving: false } && (coldest is null || file.Used < coldest.Used))
            {
                coldest = file;
            }
        }

        return coldest;
    }

    /// <summary>
    /// Removes a file that a stream created empty when it was opened, when the stream failed, so that nothing half written stays. A file that has content, or one that was there before, is left.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="placeholder">Whether the stream created the file when it was opened.</param>
    internal void Abandon(string path, bool placeholder)
    {
        List<string> garbage = [];

        lock (pool.Gate)
        {
            if (placeholder && files.TryGetValue(path, out StoredFile? file) && file.Length == 0)
            {
                Release(file, garbage);
                files.Remove(path);
            }
        }

        Erase(garbage);
    }

    /// <summary>
    /// Removes the files that were not used since a time, and the folders that were not used since then and have nothing left inside. The caller holds the lock of the pool.
    /// </summary>
    /// <param name="cutoff">Files used at or before this time are removed.</param>
    /// <param name="garbage">Receives the paths of the disk files that are no longer needed.</param>
    internal void RemoveExpired(DateTimeOffset cutoff, List<string> garbage)
    {
        foreach (KeyValuePair<string, StoredFile> entry in files.Where(entry => entry.Value is { Moving: false } && entry.Value.UsedAt <= cutoff).ToList())
        {
            Release(entry.Value, garbage);
            files.Remove(entry.Key);
        }

        foreach (string folder in folders.Where(entry => entry.Key.Length > 0 && entry.Value <= cutoff).Select(entry => entry.Key).OrderByDescending(path => path.Length).ToList())
        {
            string prefix = $"{folder}/";

            if (!files.Keys.Any(path => path.StartsWith(prefix, StringComparison.Ordinal)) && !folders.Keys.Any(path => path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                folders.Remove(folder);
            }
        }
    }

    /// <summary>
    /// Removes everything. The caller holds the lock of the pool.
    /// </summary>
    /// <param name="garbage">Receives the paths of the disk files that are no longer needed.</param>
    internal void Clear(List<string> garbage)
    {
        foreach (StoredFile file in files.Values)
        {
            Release(file, garbage);
        }

        files.Clear();

        foreach (string folder in folders.Keys.Where(path => path.Length > 0).ToList())
        {
            folders.Remove(folder);
        }
    }

    /// <summary>
    /// Sets the content of a file once a stream that wrote it is closed.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="memory">The bytes, when they are in memory.</param>
    /// <param name="diskPath">The file that holds the bytes, when they are on disk.</param>
    /// <param name="length">The size.</param>
    internal void Commit(string path, byte[]? memory, string? diskPath, long length)
    {
        List<string> garbage = [];

        lock (pool.Gate)
        {
            if (folders.ContainsKey(path))
            {
                if (diskPath is not null)
                {
                    garbage.Add(diskPath);
                }

                Erase(garbage);
                throw new IOException($"'{path}' is a folder.");
            }

            StoredFile file = new() { Memory = memory, DiskPath = diskPath, Length = length };
            Install(path, file, garbage);
            MakeParents(path);
        }

        Erase(garbage);
    }

    private static void Erase(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string Parent(string path)
        => path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;

    private void Use(StoredFile file)
    {
        file.Used = pool.Use();
        file.UsedAt = pool.Time.GetUtcNow();
    }

    private void Release(StoredFile file, List<string> garbage)
    {
        if (file.Memory is not null)
        {
            pool.Account(-file.Length);
        }

        if (file.DiskPath is not null)
        {
            garbage.Add(file.DiskPath);
        }

        file.Memory = null;
        file.DiskPath = null;
    }

    private void Install(string path, StoredFile file, List<string> garbage)
    {
        if (files.TryGetValue(path, out StoredFile? old))
        {
            Release(old, garbage);
        }

        file.TierAt = pool.Time.GetUtcNow();
        file.Hits = 0;
        Use(file);
        files[path] = file;

        if (file.Memory is not null)
        {
            pool.Account(file.Length);
        }
    }

    private void Missing(string path)
    {
        for (string parent = Parent(path); parent.Length > 0; parent = Parent(parent))
        {
            if (files.ContainsKey(parent))
            {
                throw new IOException($"'{parent}' is a file, not a folder.");
            }
        }
    }

    private void MakeParents(string path)
    {
        DateTimeOffset now = pool.Time.GetUtcNow();

        for (string parent = Parent(path); parent.Length > 0; parent = Parent(parent))
        {
            folders[parent] = now;
        }
    }

    private async Task<Stream> OpenRead(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            byte[]? memory;
            string? disk;

            lock (pool.Gate)
            {
                if (!files.TryGetValue(path, out StoredFile? file))
                {
                    throw new FileNotFoundException($"The file '{path}' does not exist.");
                }

                Use(file);
                memory = file.Memory;
                disk = file.DiskPath;

                if (memory is null)
                {
                    file.Hits++;
                    pool.Promote(file, this);
                }
            }

            if (memory is not null)
            {
                return new MemoryStream(memory, false);
            }

            try
            {
                return new FileStream(disk!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException && attempt < 4)
            {
                await Task.Yield();
            }
        }
    }

    private Task<Stream> OpenWrite(string path, StoreOpenMode mode, CancellationToken cancellation)
    {
        byte[]? seed = null;
        string? seedFile = null;
        bool placeholder = false;
        List<string> garbage = [];

        if (expires)
        {
            pool.Watch();
        }

        lock (pool.Gate)
        {
            bool exists = files.TryGetValue(path, out StoredFile? file);

            if (mode is StoreOpenMode.Open && !exists)
            {
                throw new FileNotFoundException($"The file '{path}' does not exist.");
            }

            if (path.Length == 0 || folders.ContainsKey(path))
            {
                throw new IOException($"'{path}' is a folder.");
            }

            if (mode == StoreOpenMode.Create && exists)
            {
                throw new IOException($"'{path}' already exists.");
            }

            Missing(path);

            if (exists && mode is StoreOpenMode.Open or StoreOpenMode.Append)
            {
                Use(file!);
                seed = file!.Memory;
                seedFile = file.DiskPath;
            }

            if (mode is StoreOpenMode.Create or StoreOpenMode.Replace || mode == StoreOpenMode.Append && !exists)
            {
                Install(path, new StoredFile { Memory = [], Length = 0 }, garbage);
                MakeParents(path);
                placeholder = true;
            }
        }

        Erase(garbage);
        return Task.FromResult<Stream>(new SpoolStream(this, path, seed, seedFile, mode == StoreOpenMode.Append, placeholder));
    }

    private async Task<StoredFile> Duplicate(StoredFile source, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            byte[]? memory;
            string? from;

            lock (pool.Gate)
            {
                memory = source.Memory;
                from = source.DiskPath;
            }

            if (memory is not null)
            {
                return new StoredFile { Memory = memory, Length = memory.Length };
            }

            if (from is null)
            {
                throw new IOException("The file was deleted while it was copied.");
            }

            string? folder = pool.Folder(name);

            try
            {
                if (folder is null)
                {
                    byte[] bytes = await File.ReadAllBytesAsync(from, cancellation);
                    return new StoredFile { Memory = bytes, Length = bytes.Length };
                }

                string target = Path.Combine(folder, $"{Guid.NewGuid():N}.bin");
                File.Copy(from, target);
                return new StoredFile { DiskPath = target, Length = new FileInfo(target).Length };
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException && attempt < 4)
            {
                await Task.Yield();
            }
        }
    }

    private void Relocate(string from, TieredStore other, string to)
    {
        if (from.Length == 0)
        {
            throw new UnauthorizedAccessException("The folder itself cannot be moved.");
        }

        bool file = files.ContainsKey(from);

        if (!file && !folders.ContainsKey(from))
        {
            throw new FileNotFoundException($"'{from}' does not exist.");
        }

        if (to.Length == 0 || other.files.ContainsKey(to) || other.folders.ContainsKey(to))
        {
            throw new IOException($"'{to}' already exists.");
        }

        if (!file && other == this && to.StartsWith($"{from}/", StringComparison.Ordinal))
        {
            throw new IOException($"The folder '{from}' cannot be moved into itself.");
        }

        other.Missing(to);

        void Carry(string source, string target)
        {
            StoredFile moved = files[source];
            files.Remove(source);
            other.files[target] = moved;
            other.Use(moved);
        }

        if (file)
        {
            Carry(from, to);
        }
        else
        {
            foreach (string path in files.Keys.Where(path => path.StartsWith($"{from}/", StringComparison.Ordinal)).ToList())
            {
                Carry(path, $"{to}{path[from.Length..]}");
            }

            foreach (string path in folders.Keys.Where(path => path == from || path.StartsWith($"{from}/", StringComparison.Ordinal)).ToList())
            {
                folders.Remove(path);
                other.folders[$"{to}{path[from.Length..]}"] = pool.Time.GetUtcNow();
            }
        }

        other.MakeParents(to);
    }
}
