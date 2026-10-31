namespace Markwardt.LiveAi;

/// <summary>
/// A store whose files are those of a zip archive, as <c>IStore.FromZip</c> makes it. The archive is read into memory when the store is made and behaves like <c>IStore.FromMemory</c> from then on, so the archive itself is not touched, and not locked, until <see cref="Save(CancellationToken)" /> writes it back. Nothing is saved automatically, also not when the store is dropped.
/// </summary>
public interface IZipStore : IStore
{
    /// <summary>
    /// Gets the full path of the archive file that <see cref="Save(CancellationToken)" /> writes to, or <see langword="null" /> for a store made from a stream.
    /// </summary>
    string? Path { get; }

    /// <summary>
    /// Gets a value indicating whether anything may have been changed since the store was made or last saved. It is conservative: opening a file for writing counts, whether or not anything is then written.
    /// </summary>
    bool HasChanges { get; }

    /// <summary>
    /// Writes the archive to <see cref="Path" />, creating its folder and the file if needed. The archive is written to a temporary file next to it and moved over the old one, so a failure leaves the old archive as it was.
    /// </summary>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the archive is written.</returns>
    /// <exception cref="InvalidOperationException">The store was made from a stream and has no <see cref="Path" />.</exception>
    Task Save(CancellationToken cancellation = default);

    /// <summary>
    /// Writes the archive to a stream: every file, compressed, and every folder that has nothing in it.
    /// </summary>
    /// <param name="destination">The stream to write the archive to; it is left open.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the archive is written.</returns>
    Task Save(Stream destination, CancellationToken cancellation = default);
}
