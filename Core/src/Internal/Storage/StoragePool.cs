namespace Markwardt.LiveAi;

/// <summary>
/// The storage of one session: the memory the session storage and the cache share, the folder on disk the files that do not fit move to, and the work in the background that moves them.
/// </summary>
internal interface IStoragePool : IAsyncDisposable
{
    /// <summary>
    /// Gets the lock that guards the state of every store of the pool and of every file in them.
    /// </summary>
    object Gate { get; }

    /// <summary>
    /// Gets or sets the settings the pool works with, which the session sets when a prompt starts.
    /// </summary>
    PromptStorageSettings Settings { get; set; }

    /// <summary>
    /// Gets the clock the pool and its stores work on.
    /// </summary>
    TimeProvider Time { get; }

    /// <summary>
    /// Gets how many bytes the files in memory take.
    /// </summary>
    long MemoryUsed { get; }

    /// <summary>
    /// Makes a store that shares the memory and the disk folder of the pool.
    /// </summary>
    /// <param name="name">The name of the store, which is the name of its folder on disk.</param>
    /// <param name="expires">Whether the files of the store are removed after they were not used for the retention.</param>
    /// <returns>The store.</returns>
    IStore Create(string name, bool expires);

    /// <summary>
    /// Gives the next sequence number, which orders files by how recently they were used. The caller holds <see cref="Gate" />.
    /// </summary>
    /// <returns>The number.</returns>
    long Use();

    /// <summary>
    /// Tells the pool that the files in memory take more or less bytes, and wakes the work in the background when they take too many. The caller holds <see cref="Gate" />.
    /// </summary>
    /// <param name="bytes">The change in bytes.</param>
    void Account(long bytes);

    /// <summary>
    /// Asks the work in the background to move a file read from disk back to memory, if it was read often enough and there is room. The caller holds <see cref="Gate" />.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="owner">The store that has the file.</param>
    void Promote(StoredFile file, TieredStore owner);

    /// <summary>
    /// Gives the folder of a store on disk, which is made, together with the lock on the folder of the session, when it is first asked for.
    /// </summary>
    /// <param name="name">The name of the store.</param>
    /// <returns>The folder, or <see langword="null" /> when the disk cannot be used: there is no folder to use, it is not writable, or another session holds it.</returns>
    string? Folder(string name);

    /// <summary>
    /// Reports something worth a look, such as files that could not be moved to disk.
    /// </summary>
    /// <param name="message">The message.</param>
    void Report(string message);

    /// <summary>
    /// Waits until the work in the background has nothing left to do, which a test uses to look at the state of the files.
    /// </summary>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>A task that completes when the work is idle.</returns>
    Task Settle(CancellationToken cancellation = default);

    /// <summary>
    /// Removes the files of the stores that expire which were not used for the retention.
    /// </summary>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes when the files are removed.</returns>
    Task Expire(CancellationToken cancellation = default);

    /// <summary>
    /// Starts the timer that expires files regularly, on the clock of the pool, if it is not running yet, and adds work that runs on every tick besides the expiry of the pool's own files.
    /// </summary>
    /// <param name="sweep">Work to run on every tick, or <see langword="null" /> for none.</param>
    void Watch(Action? sweep = null);
}

/// <inheritdoc />
/// <param name="time">The clock the pool and its stores work on.</param>
/// <param name="settings">The settings to start with.</param>
/// <param name="root">The folder of the session on disk (the files go in folders of their own below it), or <see langword="null" /> to keep everything in memory.</param>
/// <param name="report">Receives what is worth a look, or <see langword="null" /> to drop it.</param>
internal sealed class StoragePool(TimeProvider time, PromptStorageSettings settings, string? root, Action<string>? report = null) : IStoragePool
{
    private static readonly string[] folderNames = ["session", "cache"];

    private readonly object gate = new();

    private readonly object disk = new();

    private readonly List<TieredStore> stores = [];

    private readonly Queue<(StoredFile File, TieredStore Owner)> promotions = new();

    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly CancellationTokenSource stopping = new();

    private readonly TimeSpan residency = TimeSpan.FromSeconds(10);

    private readonly TimeSpan retry = TimeSpan.FromMinutes(1);

    private PromptStorageSettings current = settings;

    private readonly List<Action> sweeps = [];

    private Task? worker;

    private ITimer? sweeper;

    private FileStream? held;

    private long memoryUsed;

    private long sequence;

    private bool diskFailed;

    private bool disposed;

    private volatile bool pending;

    private volatile bool busy;

    private DateTimeOffset retryAfter = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public object Gate => gate;

    /// <inheritdoc />
    public TimeProvider Time => time;

    /// <inheritdoc />
    public long MemoryUsed
    {
        get
        {
            lock (gate)
            {
                return memoryUsed;
            }
        }
    }

    /// <inheritdoc />
    public PromptStorageSettings Settings
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }

        set
        {
            lock (gate)
            {
                current = value;
                retryAfter = DateTimeOffset.MinValue;
                sweeper?.Change(Interval(), Interval());
                Wake();
            }
        }
    }

    /// <summary>
    /// Removes what the sessions that ended without cleaning up left in their folders in the data folder: the folders of the sessions whose lock is not held. A folder whose name is not that of a session, and anything in a session's folder other than its storage, is left alone.
    /// </summary>
    /// <param name="dataFolder">The data folder.</param>
    public static void ClearOrphans(string dataFolder)
    {
        if (!Directory.Exists(dataFolder))
        {
            return;
        }

        foreach (string folder in Directory.EnumerateDirectories(dataFolder))
        {
            string name = System.IO.Path.GetFileName(folder);

            if (name.Length == 32 && name.All(Uri.IsHexDigit))
            {
                TryClear(folder);
            }
        }
    }

    /// <summary>
    /// Removes the storage of a session from its folder in the data folder, unless a live session holds it.
    /// </summary>
    /// <param name="folder">The folder of the session.</param>
    /// <returns><see langword="true" /> when the storage is gone or was never there, <see langword="false" /> when a live session holds it or it could not be removed.</returns>
    public static bool TryClear(string folder)
    {
        try
        {
            string lockPath = System.IO.Path.Combine(folder, "live");
            FileStream? guard = File.Exists(lockPath) ? new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

            try
            {
                foreach (string name in folderNames)
                {
                    string inside = System.IO.Path.Combine(folder, name);

                    if (Directory.Exists(inside))
                    {
                        Directory.Delete(inside, true);
                    }
                }
            }
            finally
            {
                guard?.Dispose();
            }

            if (guard is not null)
            {
                File.Delete(lockPath);
            }

            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IStore Create(string name, bool expires)
    {
        TieredStore store = new(this, name, expires);

        lock (gate)
        {
            stores.Add(store);
        }

        return store;
    }

    /// <inheritdoc />
    public long Use()
        => ++sequence;

    /// <inheritdoc />
    public void Account(long bytes)
    {
        memoryUsed += bytes;

        if (memoryUsed > current.MemoryLimit)
        {
            Wake();
        }
    }

    /// <inheritdoc />
    public void Promote(StoredFile file, TieredStore owner)
    {
        if (file.Hits < 2 || file.Queued || file.Moving || file.Length > int.MaxValue || time.GetUtcNow() - file.TierAt < residency)
        {
            return;
        }

        file.Queued = true;
        promotions.Enqueue((file, owner));
        Wake();
    }

    /// <inheritdoc />
    public string? Folder(string name)
    {
        lock (disk)
        {
            if (root is null || diskFailed || disposed)
            {
                return null;
            }

            try
            {
                if (held is null)
                {
                    Prepare();
                }

                string folder = System.IO.Path.Combine(root, name);
                MakeFolder(folder);
                return folder;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diskFailed = true;
                Report($"The disk folder of the session's storage cannot be used ({exception.Message}), so files stay in memory.");
                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Report(string message)
        => report?.Invoke(message);

    /// <inheritdoc />
    public async Task Settle(CancellationToken cancellation = default)
    {
        while (pending || busy)
        {
            await Task.Delay(2, cancellation);
        }
    }

    /// <inheritdoc />
    public Task Expire(CancellationToken cancellation = default)
    {
        List<string> garbage = [];

        lock (gate)
        {
            DateTimeOffset cutoff = time.GetUtcNow() - current.Retention;

            foreach (TieredStore store in stores.Where(store => store.Expires))
            {
                store.RemoveExpired(cutoff, garbage);
            }
        }

        foreach (string path in garbage)
        {
            Remove(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Watch(Action? sweep = null)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (sweep is not null && !sweeps.Contains(sweep))
            {
                sweeps.Add(sweep);
            }

            sweeper ??= time.CreateTimer(_ => _ = Sweep(), null, Interval(), Interval());
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sweeper?.Dispose();
            sweeper = null;
            sweeps.Clear();
        }

        await stopping.CancelAsync();
        signals.Writer.TryComplete();

        if (worker is not null)
        {
            try
            {
                await worker;
            }
            catch (OperationCanceledException)
            {
            }
        }

        List<string> garbage = [];

        lock (gate)
        {
            foreach (TieredStore store in stores)
            {
                store.Clear(garbage);
            }

            memoryUsed = 0;
            promotions.Clear();
        }

        lock (disk)
        {
            held?.Dispose();
            held = null;

            if (root is not null)
            {
                TryClear(root);
            }
        }

        stopping.Dispose();
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private TimeSpan Interval()
        => TimeSpan.FromTicks(Math.Clamp(current.Retention.Ticks / 4, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMinutes(1).Ticks));

    private async Task Sweep()
    {
        try
        {
            await Expire();
            Action[] work;

            lock (gate)
            {
                work = [.. sweeps];
            }

            foreach (Action sweep in work)
            {
                sweep();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Report($"Removing unused temporary files failed: {exception.Message}");
        }
    }

    private void Prepare()
    {
        FileStreamOptions options = new() { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };

        try
        {
            MakeFolder(root!);
            held = new FileStream(System.IO.Path.Combine(root!, "live"), options);
        }
        catch (DirectoryNotFoundException)
        {
            // The sweep of another session for ended sessions can remove the new empty folder between the two steps.
            MakeFolder(root!);
            held = new FileStream(System.IO.Path.Combine(root!, "live"), options);
        }

        foreach (string name in folderNames)
        {
            string stale = System.IO.Path.Combine(root!, name);

            if (Directory.Exists(stale))
            {
                Directory.Delete(stale, true);
            }
        }
    }

    private void MakeFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void Wake()
    {
        if (disposed)
        {
            return;
        }

        pending = true;
        worker ??= Task.Run(() => Work(stopping.Token), CancellationToken.None);
        signals.Writer.TryWrite(true);
    }

    private async Task Work(CancellationToken cancellation)
    {
        try
        {
            while (await signals.Reader.WaitToReadAsync(cancellation))
            {
                signals.Reader.TryRead(out _);
                busy = true;
                pending = false;

                try
                {
                    await Rebalance(cancellation);
                    await Promotions(cancellation);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Report($"Moving files between memory and disk failed: {exception.Message}");
                }
                finally
                {
                    busy = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Rebalance(CancellationToken cancellation)
    {
        while (true)
        {
            StoredFile? victim = null;
            TieredStore? owner = null;
            byte[]? bytes = null;

            lock (gate)
            {
                if (!disposed && memoryUsed > current.MemoryLimit && time.GetUtcNow() >= retryAfter)
                {
                    foreach (TieredStore store in stores)
                    {
                        if (store.Coldest() is { } found && (victim is null || found.Used < victim.Used))
                        {
                            victim = found;
                            owner = store;
                        }
                    }

                    if (victim is not null)
                    {
                        victim.Moving = true;
                        bytes = victim.Memory;
                    }
                }
            }

            if (victim is null || owner is null || bytes is null)
            {
                return;
            }

            await Demote(victim, owner, bytes, cancellation);
        }
    }

    private async Task Demote(StoredFile file, TieredStore owner, byte[] bytes, CancellationToken cancellation)
    {
        string? folder = Folder(owner.Name);
        string? target = folder is null ? null : System.IO.Path.Combine(folder, $"{Guid.NewGuid():N}.bin");
        bool written = false;

        if (target is not null)
        {
            try
            {
                await File.WriteAllBytesAsync(target, bytes, cancellation);
                written = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Report($"A file could not be moved to disk ({exception.Message}), so it stays in memory.");
                Remove(target);
            }
        }

        bool kept = false;

        lock (gate)
        {
            file.Moving = false;

            if (!written)
            {
                retryAfter = time.GetUtcNow() + (folder is null ? TimeSpan.FromDays(3650) : retry);
            }
            else if (ReferenceEquals(file.Memory, bytes) && file.DiskPath is null)
            {
                file.Memory = null;
                file.DiskPath = target;
                file.TierAt = time.GetUtcNow();
                file.Hits = 0;
                Account(-file.Length);
                kept = true;
            }
        }

        if (written && !kept)
        {
            Remove(target!);
        }
    }

    private async Task Promotions(CancellationToken cancellation)
    {
        while (true)
        {
            StoredFile? file = null;
            string? from = null;

            lock (gate)
            {
                while (file is null && promotions.Count > 0)
                {
                    (StoredFile candidate, _) = promotions.Dequeue();
                    candidate.Queued = false;

                    if (candidate.DiskPath is not null && !candidate.Moving && memoryUsed + candidate.Length <= current.MemoryLimit / 4 * 3 && time.GetUtcNow() - candidate.TierAt >= residency)
                    {
                        candidate.Moving = true;
                        file = candidate;
                        from = candidate.DiskPath;
                    }
                }
            }

            if (file is null || from is null)
            {
                return;
            }

            byte[]? bytes = null;

            try
            {
                bytes = await File.ReadAllBytesAsync(from, cancellation);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            bool moved = false;

            lock (gate)
            {
                file.Moving = false;

                if (bytes is not null && file.Memory is null && file.DiskPath == from)
                {
                    file.Memory = bytes;
                    file.DiskPath = null;
                    file.TierAt = time.GetUtcNow();
                    file.Hits = 0;
                    Account(file.Length);
                    moved = true;
                }
            }

            if (moved)
            {
                Remove(from);
            }
        }
    }
}
