namespace Markwardt.LiveAi;

/// <summary>
/// The folder in a workspace where the AI, and the commands it runs, put data that is wanted for a while: it is made when a session first needs it, the top level files and folders in it that were not used for a while are removed, and it is removed when the last session that uses it ends.
/// </summary>
internal interface IWorkspaceTemp : IDisposable
{
    /// <summary>
    /// Gets the full path of the folder.
    /// </summary>
    string Folder { get; }

    /// <summary>
    /// Gets a value indicating whether the folder can be used: it does not exist yet or the library made it, so a folder of the same name that something else made is never touched.
    /// </summary>
    bool IsUsable { get; }

    /// <summary>
    /// Makes sure the folder exists and tells the sweep that a command is running, so nothing is removed until it ends, and everything in the folder counts as used when it does, because what a command reads cannot be seen. A command that cannot be given the folder is counted all the same.
    /// </summary>
    /// <returns>The hold, which is disposed when the command ends.</returns>
    IDisposable Command();

    /// <summary>
    /// Notes that a path of the workspace is used. When it is in the folder the folder is made, and the top level entry it belongs to counts as used now.
    /// </summary>
    /// <param name="path">The path, relative to the workspace and using <c>/</c>.</param>
    void Touch(string path);

    /// <summary>
    /// Tells whether a path is one of the files the library keeps in the folder (the marker and the lock files), which the AI is not shown.
    /// </summary>
    /// <param name="path">The path, relative to the workspace and using <c>/</c>.</param>
    /// <returns><see langword="true" /> for the library's own files.</returns>
    bool IsInternal(string path);

    /// <summary>
    /// Looks at the top level entries, counts those that changed since the last look as used now and removes those that were not used for the retention, unless a command is running.
    /// </summary>
    void Sweep();
}

/// <inheritdoc />
/// <param name="workspace">The full path of the workspace.</param>
/// <param name="name">The name of the folder inside it.</param>
/// <param name="owner">What names the lock file of this session, which is its identifier.</param>
/// <param name="pool">The storage pool of the session, which gives the clock and the retention and runs the sweep on its timer.</param>
internal sealed class WorkspaceTemp(string workspace, string name, string owner, IStoragePool pool) : IWorkspaceTemp
{
    private readonly object gate = new();

    private readonly Dictionary<string, Seen> seen = [];

    private readonly string marker = ".gitignore";

    private readonly string lockPrefix = ".live-";

    private readonly int scanBudget = 2000;

    private FileStream? held;

    private string? heldPath;

    private int running;

    private bool ensured;

    private bool disposed;

    /// <inheritdoc />
    public string Folder => Path.Combine(workspace, name);

    /// <inheritdoc />
    public bool IsUsable
    {
        get
        {
            lock (gate)
            {
                return !disposed && (ensured || !Foreign());
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Command()
    {
        Ensure();

        lock (gate)
        {
            running++;
        }

        return new Hold(this);
    }

    /// <inheritdoc />
    public void Touch(string path)
    {
        string[] parts = (path.Resolved() ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || !string.Equals(parts[0], name, StringComparison.Ordinal) || !Ensure() || parts.Length < 2)
        {
            return;
        }

        lock (gate)
        {
            seen[parts[1]] = new Seen(seen.TryGetValue(parts[1], out Seen known) ? known.Signature : null, pool.Time.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public bool IsInternal(string path)
    {
        string[] parts = (path.Resolved() ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0] == name && (parts[1] == marker || parts[1].StartsWith(lockPrefix, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Sweep()
        => Scan(true);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            held?.Dispose();
            held = null;

            try
            {
                if (heldPath is not null)
                {
                    File.Delete(heldPath);
                }

                if (ensured && Directory.Exists(Folder) && File.Exists(Path.Combine(Folder, marker)) && !OthersAreLive())
                {
                    Directory.Delete(Folder, true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                pool.Report($"The temporary folder '{Folder}' of the workspace could not be removed ({exception.Message}).");
            }
        }
    }

    private bool Foreign()
    {
        try
        {
            return Directory.Exists(Folder) && !File.Exists(Path.Combine(Folder, marker)) && Directory.EnumerateFileSystemEntries(Folder).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private bool Ensure()
    {
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            if (ensured)
            {
                return true;
            }

            try
            {
                if (Foreign())
                {
                    return false;
                }

                Directory.CreateDirectory(Folder);
                string mark = Path.Combine(Folder, marker);

                if (!File.Exists(mark))
                {
                    File.WriteAllText(mark, "*\n");
                }

                heldPath = Path.Combine(Folder, lockPrefix + owner);
                held = new FileStream(heldPath, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None });

                if (!OthersAreLive())
                {
                    Clear();
                }

                ensured = true;
                pool.Watch(Sweep);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                held?.Dispose();
                held = null;
                pool.Report($"The temporary folder '{Folder}' of the workspace cannot be used ({exception.Message}).");
                return false;
            }
        }
    }

    private bool OthersAreLive()
    {
        bool live = false;

        foreach (string path in Directory.EnumerateFiles(Folder, lockPrefix + "*"))
        {
            if (path == heldPath)
            {
                continue;
            }

            try
            {
                using (new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.None }))
                {
                }

                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                live = true;
            }
        }

        return live;
    }

    private void Clear()
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(Folder))
        {
            string entry = Path.GetFileName(path);

            if (entry != marker && !entry.StartsWith(lockPrefix, StringComparison.Ordinal))
            {
                Remove(path);
            }
        }
    }

    private void Remove(string path)
    {
        try
        {
            if (Directory.Exists(path) && !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            pool.Report($"'{path}' in the temporary folder of the workspace could not be removed ({exception.Message}).");
        }
    }

    private void Scan(bool remove)
    {
        lock (gate)
        {
            if (!ensured || disposed)
            {
                return;
            }

            DateTimeOffset now = pool.Time.GetUtcNow();
            DateTimeOffset cutoff = now - pool.Settings.Retention;
            HashSet<string> present = [];

            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(Folder).ToList())
                {
                    string entry = Path.GetFileName(path);

                    if (entry == marker || entry.StartsWith(lockPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    present.Add(entry);
                    string signature = Signature(path);

                    if (!seen.TryGetValue(entry, out Seen known))
                    {
                        seen[entry] = new Seen(signature, now);
                    }
                    else if (known.Signature is null)
                    {
                        seen[entry] = known with { Signature = signature };
                    }
                    else if (known.Signature != signature)
                    {
                        seen[entry] = new Seen(signature, now);
                    }
                    else if (remove && running == 0 && known.Used <= cutoff)
                    {
                        Remove(path);
                        seen.Remove(entry);
                        present.Remove(entry);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (string gone in seen.Keys.Where(entry => !present.Contains(entry)).ToList())
            {
                seen.Remove(gone);
            }
        }
    }

    private string Signature(string path)
    {
        long count = 0;
        long bytes = 0;
        long latest = 0;
        bool cut = false;

        try
        {
            FileSystemInfo top = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            List<FileSystemInfo> infos = [top];

            if (top is DirectoryInfo folder)
            {
                EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                infos.AddRange(folder.EnumerateFileSystemInfos("*", options).Take(scanBudget + 1));
            }

            cut = infos.Count > scanBudget;

            foreach (FileSystemInfo info in infos.Take(scanBudget))
            {
                count++;
                latest = Math.Max(latest, info.LastWriteTimeUtc.Ticks);

                if (info is FileInfo file)
                {
                    bytes += file.Length;
                    latest = Math.Max(latest, file.LastAccessTimeUtc.Ticks);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }

        return $"{count}:{bytes}:{latest}:{cut}";
    }

    private void Release()
    {
        lock (gate)
        {
            running--;
            DateTimeOffset now = pool.Time.GetUtcNow();

            foreach (string entry in seen.Keys.ToList())
            {
                seen[entry] = seen[entry] with { Used = now };
            }
        }

        Scan(false);
    }

    private sealed class Hold(WorkspaceTemp owner) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release();
            }
        }
    }

    private readonly record struct Seen(string? Signature, DateTimeOffset Used);
}
