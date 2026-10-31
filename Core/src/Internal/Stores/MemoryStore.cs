namespace Markwardt.LiveAi;

/// <summary>
/// Files and folders kept in memory. Folders are real: one can be empty, moved and deleted.
/// </summary>
internal sealed class MemoryStore : IStore, IStoreTransfer
{
    private static readonly object transfers = new();

    private readonly object gate = new();

    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

    private readonly HashSet<string> folders = new(StringComparer.Ordinal) { string.Empty };

    /// <inheritdoc />
    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (!folders.Contains(directory))
            {
                throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");
            }

            string prefix = directory.Length == 0 ? string.Empty : $"{directory}/";
            bool Inside(string path)
                => recursive ? path.StartsWith(prefix, StringComparison.Ordinal) : Parent(path) == directory;

            return files.Keys.Where(Inside).Select(path => new StoreEntry(path[prefix.Length..], false)).Concat(folders.Where(path => path.Length > 0 && Inside(path)).Select(path => new StoreEntry(path[prefix.Length..], true))).ToList().ToAsyncEnumerable();
        }
    }

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            return Task.FromResult(folders.Contains(path));
        }
    }

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (files.TryGetValue(path, out byte[]? content))
            {
                return Task.FromResult((long)content.Length);
            }

            if (!folders.Contains(path))
            {
                throw new FileNotFoundException($"'{path}' does not exist.");
            }

            string prefix = path.Length == 0 ? string.Empty : $"{path}/";
            return Task.FromResult(files.Where(file => file.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(file => (long)file.Value.Length));
        }
    }

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (from.Length == 0 || to.Length == 0)
            {
                throw new ArgumentException("The root of the store itself cannot be copied or be the destination.");
            }

            bool file = files.ContainsKey(from);

            if (!file && !folders.Contains(from))
            {
                throw new FileNotFoundException($"'{from}' does not exist.");
            }

            if (files.ContainsKey(to) || folders.Contains(to))
            {
                throw new IOException($"'{to}' already exists.");
            }

            if (!file && to.StartsWith($"{from}/", StringComparison.Ordinal))
            {
                throw new IOException($"The folder '{from}' cannot be copied into itself.");
            }

            List<string> parents = Missing(to);

            if (file)
            {
                files[to] = files[from].ToArray();
            }
            else
            {
                foreach (string path in files.Keys.Where(path => path.StartsWith($"{from}/", StringComparison.Ordinal)).ToList())
                {
                    files[$"{to}{path[from.Length..]}"] = files[path].ToArray();
                }

                foreach (string path in folders.Where(path => path == from || path.StartsWith($"{from}/", StringComparison.Ordinal)).ToList())
                {
                    folders.Add($"{to}{path[from.Length..]}");
                }
            }

            folders.UnionWith(parents);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        lock (gate)
        {
            bool exists = files.TryGetValue(path, out byte[]? content);

            if (mode is StoreOpenMode.Read or StoreOpenMode.Open && !exists)
            {
                throw new FileNotFoundException($"The file '{path}' does not exist.");
            }

            if (mode == StoreOpenMode.Read)
            {
                return Task.FromResult<Stream>(new MemoryStream(content!, false));
            }

            if (mode == StoreOpenMode.Create && exists)
            {
                throw new IOException($"'{path}' already exists.");
            }

            if (mode != StoreOpenMode.Open)
            {
                Put(path, mode == StoreOpenMode.Append && exists ? content! : []);
            }

            return Task.FromResult<Stream>(new CommitStream(mode is StoreOpenMode.Open or StoreOpenMode.Append ? content ?? [] : [], mode == StoreOpenMode.Append, bytes =>
            {
                lock (gate)
                {
                    Put(path, bytes);
                }
            }));
        }
    }

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (path.Length == 0)
            {
                throw new UnauthorizedAccessException("The folder itself cannot be deleted.");
            }

            if (files.Remove(path))
            {
                return Task.CompletedTask;
            }

            if (!folders.Contains(path))
            {
                throw new FileNotFoundException($"'{path}' does not exist.");
            }

            foreach (string inside in files.Keys.Where(other => other.StartsWith($"{path}/", StringComparison.Ordinal)).ToList())
            {
                files.Remove(inside);
            }

            folders.RemoveWhere(other => other == path || other.StartsWith($"{path}/", StringComparison.Ordinal));
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (files.ContainsKey(path))
            {
                throw new IOException($"'{path}' is a file, not a folder.");
            }

            if (path.Length > 0)
            {
                folders.UnionWith(Missing(path));
                folders.Add(path);
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            Relocate(from, this, to);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<bool> TryMoveTo(string from, IStore target, string to, CancellationToken cancellation = default)
    {
        if (target is not MemoryStore other || other == this)
        {
            return Task.FromResult(false);
        }

        lock (transfers)
        {
            lock (gate)
            {
                lock (other.gate)
                {
                    Relocate(from, other, to);
                    return Task.FromResult(true);
                }
            }
        }
    }

    private string Parent(string path)
        => path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;

    private void Put(string path, byte[] content)
    {
        if (path.Length == 0 || folders.Contains(path))
        {
            throw new IOException($"'{path}' is a folder.");
        }

        Parents(path);
        files[path] = content;
    }

    private void Parents(string path)
        => folders.UnionWith(Missing(path));

    private List<string> Missing(string path)
    {
        List<string> missing = [];

        for (string parent = Parent(path); parent.Length > 0; parent = Parent(parent))
        {
            if (files.ContainsKey(parent))
            {
                throw new IOException($"'{parent}' is a file, not a folder.");
            }

            if (!folders.Contains(parent))
            {
                missing.Add(parent);
            }
        }

        return missing;
    }

    private void Relocate(string from, MemoryStore other, string to)
    {
        if (from.Length == 0)
        {
            throw new UnauthorizedAccessException("The folder itself cannot be moved.");
        }

        bool file = files.ContainsKey(from);

        if (!file && !folders.Contains(from))
        {
            throw new FileNotFoundException($"'{from}' does not exist.");
        }

        if (to.Length == 0 || other.files.ContainsKey(to) || other.folders.Contains(to))
        {
            throw new IOException($"'{to}' already exists.");
        }

        if (!file && other == this && to.StartsWith($"{from}/", StringComparison.Ordinal))
        {
            throw new IOException($"The folder '{from}' cannot be moved into itself.");
        }

        List<string> parents = other.Missing(to);

        if (file)
        {
            other.files[to] = files[from];
            files.Remove(from);
        }
        else
        {
            foreach (string path in files.Keys.Where(path => path.StartsWith($"{from}/", StringComparison.Ordinal)).ToList())
            {
                other.files[$"{to}{path[from.Length..]}"] = files[path];
                files.Remove(path);
            }

            foreach (string path in folders.Where(path => path == from || path.StartsWith($"{from}/", StringComparison.Ordinal)).ToList())
            {
                folders.Remove(path);
                other.folders.Add($"{to}{path[from.Length..]}");
            }
        }

        other.folders.UnionWith(parents);
    }
}
