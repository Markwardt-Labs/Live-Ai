namespace Markwardt.LiveAi;

/// <summary>
/// The files of a folder on disk. A relative path is relative to the folder and an absolute path is a path on this machine. In a sandbox only what lies inside the folder is reachable: a path that leads outside, also through a link, is refused. Without one every path on the machine is.
/// </summary>
/// <param name="root">The full path of the folder.</param>
/// <param name="sandbox">Whether only paths inside the folder are accepted.</param>
internal sealed class DiskStore(string root, bool sandbox = true) : IStore, IStoreTransfer
{
    /// <summary>
    /// Makes a store of an existing folder.
    /// </summary>
    /// <param name="path">The folder, relative to the current directory or absolute.</param>
    /// <param name="sandbox">Whether only paths inside the folder are accepted.</param>
    /// <returns>The store.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public static DiskStore Open(string path, bool sandbox = true)
        => Directory.Exists(Path.GetFullPath(path)) ? new DiskStore(Path.GetFullPath(path), sandbox) : throw new DirectoryNotFoundException($"The folder '{path}' does not exist.");

    private string? canonicalRoot;

    /// <summary>
    /// Gets the full path of the folder.
    /// </summary>
    public string Folder => root;

    /// <summary>
    /// Gets a value indicating whether only paths inside the folder are accepted.
    /// </summary>
    public bool IsSandboxed => sandbox;

    private string Root => canonicalRoot ??= Canonical(Path.GetFullPath(root));

    /// <inheritdoc />
    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
    {
        string path = Resolve(directory);

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");
        }

        return Enumerate(path, string.Empty, recursive, [path]).ToAsyncEnumerable();
    }

    /// <inheritdoc />
    public Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => Task.FromResult(Directory.Exists(Resolve(path)));

    /// <inheritdoc />
    public Task<long> GetSize(string path, CancellationToken cancellation = default)
    {
        string full = Resolve(path);

        if (File.Exists(full))
        {
            return Task.FromResult(new FileInfo(full).Length);
        }

        if (!Directory.Exists(full))
        {
            throw new FileNotFoundException($"'{path}' does not exist.");
        }

        return Task.FromResult(Enumerate(full, string.Empty, true, [full]).Where(entry => !entry.IsDirectory).Sum(entry => new FileInfo(Path.Combine(full, entry.Path)).Length));
    }

    /// <inheritdoc />
    public Task Copy(string from, string to, CancellationToken cancellation = default)
    {
        string source = Resolve(from);
        string destination = Resolve(to);

        if (source == Root || destination == Root || Path.GetPathRoot(destination) == destination)
        {
            throw new ArgumentException("The root of the store itself cannot be copied or be the destination.");
        }

        bool file = File.Exists(source);

        if (!file && !Directory.Exists(source))
        {
            throw new FileNotFoundException($"'{from}' does not exist.");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"'{to}' already exists.");
        }

        if (!file && destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The folder '{from}' cannot be copied into itself.");
        }

        EnsureFolder(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        try
        {
            if (file)
            {
                File.Copy(source, destination);
            }
            else
            {
                CopyTree(source, destination, [source]);
            }
        }
        catch
        {
            try
            {
                if (file)
                {
                    File.Delete(destination);
                }
                else
                {
                    Directory.Delete(destination, true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        string full = Resolve(path);
        bool existing = mode is StoreOpenMode.Read or StoreOpenMode.Open;

        if (existing && !File.Exists(full))
        {
            throw new FileNotFoundException($"The file '{path}' does not exist.");
        }

        if (!existing)
        {
            if (Directory.Exists(full))
            {
                throw new IOException($"'{path}' is a folder.");
            }

            if (mode == StoreOpenMode.Create && File.Exists(full))
            {
                throw new IOException($"'{path}' already exists.");
            }

            EnsureFolder(Path.GetDirectoryName(full)!);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        }

        return Task.FromResult<Stream>(mode switch
        {
            StoreOpenMode.Read => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan),
            StoreOpenMode.Open => new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous),
            StoreOpenMode.Create => new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous),
            StoreOpenMode.Replace => new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous),
            StoreOpenMode.Append => new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        });
    }

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation = default)
    {
        string full = Resolve(path);

        if (full == Root || Path.GetPathRoot(full) == full)
        {
            throw new UnauthorizedAccessException("The folder itself cannot be deleted.");
        }

        if (File.Exists(full))
        {
            File.Delete(full);
        }
        else if (Directory.Exists(full))
        {
            Directory.Delete(full, true);
        }
        else
        {
            throw new FileNotFoundException($"'{path}' does not exist.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CreateFolder(string path, CancellationToken cancellation = default)
    {
        string full = Resolve(path);

        if (File.Exists(full))
        {
            throw new IOException($"'{path}' is a file, not a folder.");
        }

        EnsureFolder(Path.GetDirectoryName(full)!);
        Directory.CreateDirectory(full);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Move(string from, string to, CancellationToken cancellation = default)
    {
        Relocate(from, this, to);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryMoveTo(string from, IStore target, string to, CancellationToken cancellation = default)
        => Task.FromResult(target is DiskStore other && Relocate(from, other, to));

    private bool Relocate(string from, DiskStore other, string to)
    {
        string source = Resolve(from);
        string destination = other.Resolve(to);

        if (source == Root || Path.GetPathRoot(source) == source)
        {
            throw new UnauthorizedAccessException("The folder itself cannot be moved.");
        }

        bool file = File.Exists(source);

        if (!file && !Directory.Exists(source))
        {
            throw new FileNotFoundException($"'{from}' does not exist.");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"'{to}' already exists.");
        }

        if (!file && destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The folder '{from}' cannot be moved into itself.");
        }

        other.EnsureFolder(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (file)
        {
            File.Move(source, destination);
            return true;
        }

        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (IOException) when (Directory.Exists(source) && !Directory.Exists(destination))
        {
            return false;
        }
    }

    private void CopyTree(string source, string destination, List<string> ancestors)
    {
        Directory.CreateDirectory(destination);

        foreach (FileSystemInfo info in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            string inside = Resolve(info.FullName);
            string copy = Path.Combine(destination, info.Name);

            if (!info.Attributes.HasFlag(FileAttributes.Directory))
            {
                File.Copy(inside, copy);
            }
            else if (ancestors.Contains(inside))
            {
                throw new IOException($"The folder '{Path.GetRelativePath(Root, info.FullName).Replace(Path.DirectorySeparatorChar, '/')}' is a link back to a folder that is being copied.");
            }
            else
            {
                ancestors.Add(inside);
                CopyTree(inside, copy, ancestors);
                ancestors.RemoveAt(ancestors.Count - 1);
            }
        }
    }

    private IEnumerable<StoreEntry> Enumerate(string path, string prefix, bool recursive, HashSet<string> visited)
    {
        foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            bool folder = info.Attributes.HasFlag(FileAttributes.Directory);
            yield return new StoreEntry(prefix + info.Name, folder);

            if (recursive && folder && Enter(info.FullName) is { } inside && visited.Add(inside))
            {
                foreach (StoreEntry nested in Enumerate(inside, $"{prefix}{info.Name}/", true, visited))
                {
                    yield return nested;
                }
            }
        }
    }

    private string? Enter(string full)
    {
        try
        {
            return Resolve(full);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void EnsureFolder(string full)
    {
        for (string folder = full; folder != Root && folder.StartsWith(Root, StringComparison.Ordinal); folder = Path.GetDirectoryName(folder)!)
        {
            if (File.Exists(folder))
            {
                throw new IOException($"'{Path.GetRelativePath(Root, folder).Replace(Path.DirectorySeparatorChar, '/')}' is a file, not a folder.");
            }
        }
    }

    private string Resolve(string relative)
    {
        string canonical = Canonical(Path.GetFullPath(Path.Combine(Root, relative)));

        if (sandbox && canonical != Root && !canonical.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"'{relative}' is outside the folder.");
        }

        return canonical;
    }

    private string Canonical(string full)
    {
        string current = Path.GetPathRoot(full)!;

        foreach (string segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);

            if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
            {
                current = target.FullName;
            }
        }

        return current;
    }
}
