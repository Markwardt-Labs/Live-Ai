namespace Markwardt.LiveAi;

/// <summary>
/// Files and folders the library's file tools work on. A caller supplies one for a folder on disk (<c>IStore.FromDisk</c>) or implements it for documents in a database, an in-memory copy or a remote store, and mounts it to a session by name. Paths are relative to the root of the store, use <c>/</c> and have no <c>.</c> or <c>..</c> segments; the empty string is the root itself. An exception's message is shown to the AI as the tool's error, so it should say what went wrong.
/// </summary>
public interface IStore
{
    /// <summary>
    /// Lists the files and folders directly in a folder, or everything below it.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <param name="recursive">Whether to list everything below the folder, at any depth, and not only what is directly in it. The path of an entry is relative to <paramref name="directory" /> either way.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The entries, in any order, as they are found.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default);

    /// <summary>
    /// Opens a file as a stream of bytes, for reading, writing or both as the mode says. Use <c>ReadText</c> and <c>WriteText</c> for the text of a file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="mode">How to open it, which decides whether it must exist, whether it is made, and what the stream allows.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A stream the caller disposes. It can be read in <see cref="StoreOpenMode.Read" /> and <see cref="StoreOpenMode.Open" /> mode and written in every mode except <see cref="StoreOpenMode.Read" />; a store may allow more than that. Writes are guaranteed to be stored once the stream is disposed.</returns>
    /// <exception cref="FileNotFoundException">The mode needs the file to exist and it does not.</exception>
    /// <exception cref="IOException">The mode is <see cref="StoreOpenMode.Create" /> and the path is taken, or the path is a folder.</exception>
    Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default);

    /// <summary>
    /// Makes a folder and the folders above it. Does nothing if the folder is already there, also when it is empty.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the folder is there.</returns>
    /// <exception cref="IOException">The path, or a folder above it, is a file.</exception>
    Task CreateFolder(string path, CancellationToken cancellation = default);

    /// <summary>
    /// Deletes a file, or a folder with everything in it.
    /// </summary>
    /// <param name="path">The file or folder.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once it is deleted.</returns>
    Task Delete(string path, CancellationToken cancellation = default);

    /// <summary>
    /// Moves or renames a file or folder, without replacing anything. A folder is moved with everything in it, empty folders included, and nothing is copied: the content stays where it is and only its place changes.
    /// </summary>
    /// <param name="from">The file or folder.</param>
    /// <param name="to">Where it goes; nothing may be there.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once it is moved.</returns>
    Task Move(string from, string to, CancellationToken cancellation = default);

    /// <summary>
    /// Tells whether a path is a folder. The default looks the path up in the listing of the folder above it; a store that can tell more cheaply should implement it.
    /// </summary>
    /// <param name="path">The path to look at; the empty string is the root of the store.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns><see langword="true" /> for a folder, <see langword="false" /> for a file and for something that does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> leads outside the store (the default only).</exception>
    Task<bool> IsFolder(string path, CancellationToken cancellation = default)
        => StoreExtensions.DefaultIsFolder(this, path, cancellation);

    /// <summary>
    /// Gets the size in bytes of a file, or of everything in a folder. The default opens each file and takes the length of its stream (counting the bytes if the stream cannot seek); a store that knows sizes without reading should implement it.
    /// </summary>
    /// <param name="path">The file or folder; the empty string is the root of the store.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The number of bytes of the file, or the sum over all the files in the folder and the folders in it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> leads outside the store (the default only).</exception>
    /// <exception cref="FileNotFoundException"><paramref name="path" /> does not exist.</exception>
    Task<long> GetSize(string path, CancellationToken cancellation = default)
        => StoreExtensions.DefaultGetSize(this, path, cancellation);

    /// <summary>
    /// Copies a file, or a folder with everything in it, without replacing anything. Files are copied byte for byte and empty folders are copied too. A copy that fails is removed again, so there is no half of one left behind. The default copies through streams, folders and listings; a store that can copy natively (a file system, a database) should implement it.
    /// </summary>
    /// <param name="from">The file or folder to copy.</param>
    /// <param name="to">Where the copy goes; nothing may be there.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once everything is copied.</returns>
    /// <exception cref="ArgumentException">A path is the root of the store itself, or leads outside it (the default only).</exception>
    /// <exception cref="FileNotFoundException"><paramref name="from" /> does not exist.</exception>
    /// <exception cref="IOException"><paramref name="to" /> already exists, or is inside the folder being copied.</exception>
    Task Copy(string from, string to, CancellationToken cancellation = default)
        => StoreExtensions.DefaultCopy(this, from, to, cancellation);
}

/// <summary>
/// Operations built from the <see cref="IStore" /> ones, so every store has them: the text of a file, listing only files or folders, and a read-only view.
/// </summary>
public static class StoreExtensions
{
    extension(IStore)
    {
        /// <summary>
        /// Makes a store that keeps files and folders in memory, for a session that works on documents that are not on disk or for a place to put what the AI makes. Read what is in it with <see cref="IStore.List" /> and <c>ReadText</c>.
        /// </summary>
        /// <returns>The store, which starts empty.</returns>
        public static IStore FromMemory()
            => new MemoryStore();

        /// <summary>
        /// Makes a store of the files of a zip archive, which is read into memory (a file that does not exist is an empty archive). Changes stay in memory until <see cref="IZipStore.Save(CancellationToken)" /> writes the archive back. The whole archive is kept in memory, so this suits archives of moderate size, and nothing limits how large the files in an archive may expand to.
        /// </summary>
        /// <param name="path">The archive file; it may not exist yet.</param>
        /// <param name="cancellation">Cancels the reading.</param>
        /// <returns>The store of the archive.</returns>
        /// <exception cref="InvalidDataException">The file is not a valid archive, or an entry is named with a path that is absolute or leads outside the archive.</exception>
        public static async Task<IZipStore> FromZip(string path, CancellationToken cancellation = default)
        {
            string full = Path.GetFullPath(path);
            ZipStore store = new(full);

            if (File.Exists(full))
            {
                await using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
                await store.Load(stream, cancellation);
            }

            return store;
        }

        /// <summary>
        /// Makes a store of the files of a zip archive that is read from a stream. It has no file, so it is saved to a stream.
        /// </summary>
        /// <param name="source">The archive, which is read to its end and left open.</param>
        /// <param name="cancellation">Cancels the reading.</param>
        /// <returns>The store of the archive.</returns>
        /// <exception cref="InvalidDataException">The data is not a valid archive, or an entry is named with a path that is absolute or leads outside the archive.</exception>
        public static async Task<IZipStore> FromZip(Stream source, CancellationToken cancellation = default)
        {
            ZipStore store = new(null);
            await store.Load(source, cancellation);
            return store;
        }

        /// <summary>
        /// Makes a store of a folder on disk. A relative path is relative to the folder and an absolute path is a path on this machine. Reads over 10 MB are refused.
        /// </summary>
        /// <param name="path">The folder, which must exist.</param>
        /// <param name="sandbox">Whether only paths that land inside the folder are accepted (the default): a path that leads outside, also through a link, is refused. When it is <see langword="false" /> any path on this machine is valid, absolute or relative, and the folder is only where relative paths start.</param>
        /// <returns>The store of the folder, which reveals its path.</returns>
        /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
        public static IDiskStore FromDisk(string path, bool sandbox = true)
            => Directory.Exists(Path.GetFullPath(path)) ? new DiskStore(Path.GetFullPath(path), sandbox) : throw new DirectoryNotFoundException($"The folder '{path}' does not exist.");
    }

    extension(IStore store)
    {
        /// <summary>
        /// Reads a whole file as text, decoding it as UTF-8 (or as the encoding its byte order mark names).
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="cancellation">Cancels the work.</param>
        /// <returns>The text of the file.</returns>
        public async Task<string> ReadText(string path, CancellationToken cancellation = default)
        {
            await using Stream stream = await store.Open(path, StoreOpenMode.Read, cancellation);
            using StreamReader reader = new(stream, Encoding.UTF8, true);
            return await reader.ReadToEndAsync(cancellation);
        }

        /// <summary>
        /// Reads a file of a store as an attachment for a prompt, taking the media type from its name (<c>application/octet-stream</c> when the extension is not a known one).
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="cancellation">Cancels the work.</param>
        /// <returns>The attachment, named after the file.</returns>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        public async Task<PromptAttachment> ReadAttachment(string path, CancellationToken cancellation = default)
        {
            await using Stream stream = await store.Open(path, StoreOpenMode.Read, cancellation);
            using MemoryStream content = new();
            await stream.CopyToAsync(content, cancellation);
            string name = path[(path.LastIndexOf('/') + 1)..];
            return new PromptAttachment { Name = name, MediaType = name.ToMediaType(), Content = content.ToArray() };
        }

        /// <summary>
        /// Writes a file from text, encoded as UTF-8 without a byte order mark, creating it and its folders or replacing its content.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="text">The new text of the file.</param>
        /// <param name="cancellation">Cancels the work.</param>
        /// <returns>A task that completes once the file is written.</returns>
        public async Task WriteText(string path, string text, CancellationToken cancellation = default)
        {
            await using Stream stream = await store.Open(path, StoreOpenMode.Replace, cancellation);
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellation);
        }

        /// <summary>
        /// Lists the files in a folder, without the folders.
        /// </summary>
        /// <param name="directory">The folder.</param>
        /// <param name="recursive">Whether to list the files at any depth below the folder; their paths are relative to it.</param>
        /// <param name="cancellation">Cancels the work.</param>
        /// <returns>The paths of the files, relative to <paramref name="directory" />, in any order, as they are found.</returns>
        /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
        public IAsyncEnumerable<string> ListFiles(string directory, bool recursive = false, CancellationToken cancellation = default)
            => store.List(directory, recursive, cancellation).Where(entry => !entry.IsDirectory).Select(entry => entry.Path);

        /// <summary>
        /// Lists the folders in a folder, without the files.
        /// </summary>
        /// <param name="directory">The folder.</param>
        /// <param name="recursive">Whether to list the folders at any depth below the folder; their paths are relative to it.</param>
        /// <param name="cancellation">Cancels the work.</param>
        /// <returns>The paths of the folders, relative to <paramref name="directory" />, in any order, as they are found.</returns>
        /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
        public IAsyncEnumerable<string> ListFolders(string directory, bool recursive = false, CancellationToken cancellation = default)
            => store.List(directory, recursive, cancellation).Where(entry => entry.IsDirectory).Select(entry => entry.Path);

        /// <summary>
        /// Wraps a store so that every access to it is reported to a function: listings, opening a file to read or to change it, making folders, deleting, moving and copying. An entry is reported after the access, with the error if it failed. A file is reported when it is opened, not when the stream is closed. Nothing is changed by the wrapper; an exception of the function reaches the caller of the access (the AI sees it as the tool's error) after the access has been made.
        /// </summary>
        /// <param name="sink">Receives each access.</param>
        /// <returns>The audited view of the store.</returns>
        public IStore Audited(Func<StoreAuditEntry, CancellationToken, Task> sink)
            => new AuditedStore(store, sink);

        /// <summary>
        /// Wraps a store like <see cref="Audited(IStore, Func{StoreAuditEntry, CancellationToken, Task})" /> and reports each access to an action.
        /// </summary>
        /// <param name="sink">Receives each access.</param>
        /// <returns>The audited view of the store.</returns>
        public IStore Audited(Action<StoreAuditEntry> sink)
            => new AuditedStore(store, (entry, _) =>
            {
                sink(entry);
                return Task.CompletedTask;
            });

        /// <summary>
        /// Puts an overlay on a store, so that changes to it are kept in memory until they are applied or thrown away (see <see cref="IOverlayStore" />).
        /// </summary>
        /// <returns>The overlay, which starts without changes.</returns>
        public IOverlayStore AsOverlay()
            => new OverlayStore(store);

        /// <summary>
        /// Wraps a store so that it can be read but not changed: writing, creating folders, deleting and moving fail with a message that says so.
        /// </summary>
        /// <returns>The read-only view of the store.</returns>
        public IStore AsReadOnly()
            => new ReadOnlyStore(store);
    }

    /// <summary>
    /// Tells whether a path is a folder, from a listing of the folder above it.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="path">The path to look at.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>Whether the path is a folder.</returns>
    internal static async Task<bool> DefaultIsFolder(IStore store, string path, CancellationToken cancellation = default)
    {
        string resolved = path.Resolved() ?? throw new ArgumentException($"'{path}' leads outside the store.", nameof(path));
        return resolved.Length == 0 || (await Find(store, resolved, cancellation))?.IsDirectory == true;
    }

    /// <summary>
    /// Gets the size of a file, or of a folder, by reading what the store lists and opens.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="path">The file or folder.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The size in bytes.</returns>
    internal static async Task<long> DefaultGetSize(IStore store, string path, CancellationToken cancellation = default)
    {
        string resolved = path.Resolved() ?? throw new ArgumentException($"'{path}' leads outside the store.", nameof(path));

        if (resolved.Length == 0 || (await Find(store, resolved, cancellation))?.IsDirectory == true)
        {
            return await FolderSize(store, resolved, cancellation);
        }

        return await FileSize(store, resolved, cancellation);
    }

    /// <summary>
    /// Copies a file or folder through the streams, folders and listings of the store, removing a half made copy if it fails.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="from">The file or folder to copy.</param>
    /// <param name="to">Where the copy goes.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once everything is copied.</returns>
    internal static async Task DefaultCopy(IStore store, string from, string to, CancellationToken cancellation = default)
    {
        string source = from.Resolved() ?? throw new ArgumentException($"'{from}' leads outside the store.", nameof(from));
        string target = to.Resolved() ?? throw new ArgumentException($"'{to}' leads outside the store.", nameof(to));

        if (source.Length == 0 || target.Length == 0)
        {
            throw new ArgumentException("The root of the store itself cannot be copied or be the destination.");
        }

        StoreEntry entry = await Find(store, source, cancellation) ?? throw new FileNotFoundException($"'{from}' does not exist.");

        if (await Find(store, target, cancellation) is not null)
        {
            throw new IOException($"'{to}' already exists.");
        }

        if (!entry.IsDirectory)
        {
            await CopyFile(store, source, target, cancellation);
        }
        else if (target.StartsWith($"{source}/", StringComparison.Ordinal))
        {
            throw new IOException($"The folder '{from}' cannot be copied into itself.");
        }
        else
        {
            try
            {
                await CopyFolder(store, source, target, cancellation);
            }
            catch
            {
                try
                {
                    await store.Delete(target, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Looks an entry up through the listing of its folder.
    /// </summary>
    /// <param name="store">The store to look in.</param>
    /// <param name="path">The normalized path of the entry.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The entry, or <see langword="null" /> when there is none.</returns>
    internal static async Task<StoreEntry?> Find(IStore store, string path, CancellationToken cancellation = default)
    {
        int slash = path.LastIndexOf('/');

        try
        {
            await foreach (StoreEntry entry in store.List(slash < 0 ? string.Empty : path[..slash], false, cancellation))
            {
                if (entry.Path == path[(slash + 1)..])
                {
                    return entry;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        return null;
    }

    private static async Task<long> FileSize(IStore store, string path, CancellationToken cancellation = default)
    {
        await using Stream stream = await store.Open(path, StoreOpenMode.Read, cancellation);

        if (stream.CanSeek)
        {
            return stream.Length;
        }

        long total = 0;
        byte[] buffer = new byte[81920];

        for (int read = await stream.ReadAsync(buffer, cancellation); read > 0; read = await stream.ReadAsync(buffer, cancellation))
        {
            total += read;
        }

        return total;
    }

    private static async Task<long> FolderSize(IStore store, string path, CancellationToken cancellation = default)
    {
        long total = 0;

        await foreach (string file in store.ListFiles(path, true, cancellation))
        {
            total += await FileSize(store, path.Length == 0 ? file : $"{path}/{file}", cancellation);
        }

        return total;
    }

    private static async Task CopyFile(IStore store, string from, string to, CancellationToken cancellation = default)
    {
        await using Stream source = await store.Open(from, StoreOpenMode.Read, cancellation);
        Stream target = await store.Open(to, StoreOpenMode.Create, cancellation);

        try
        {
            await using (target)
            {
                await source.CopyToAsync(target, cancellation);
            }
        }
        catch
        {
            try
            {
                await store.Delete(to, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private static async Task CopyFolder(IStore store, string from, string to, CancellationToken cancellation = default)
    {
        await store.CreateFolder(to, cancellation);

        foreach (StoreEntry entry in await store.List(from, false, cancellation).ToListAsync(cancellation))
        {
            if (entry.IsDirectory)
            {
                await CopyFolder(store, $"{from}/{entry.Path}", $"{to}/{entry.Path}", cancellation);
            }
            else
            {
                await CopyFile(store, $"{from}/{entry.Path}", $"{to}/{entry.Path}", cancellation);
            }
        }
    }
}
