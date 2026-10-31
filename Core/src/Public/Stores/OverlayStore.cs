namespace Markwardt.LiveAi;

/// <summary>
/// A store that shows another one with changes on top, which are kept in memory and only reach the store underneath when they are applied. What is read comes from the changes where there are any and from the store underneath otherwise; what is written, deleted or moved only changes the overlay. That lets the AI work freely on a folder, a zip or a database while the caller looks at <see cref="GetChanges" /> and then chooses between <see cref="Apply" /> and <see cref="Discard" />. Make one with <see cref="StoreExtensions.AsOverlay(IStore)" />.
/// </summary>
public interface IOverlayStore : IStore
{
    /// <summary>
    /// Lists what the overlay would change in the store under it: files and folders that are added, files whose content differs, and files and folders that are deleted (a deleted folder as one change). A file that was changed and then changed back is not listed.
    /// </summary>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The changes, ordered by path.</returns>
    Task<IReadOnlyList<StoreChange>> GetChanges(CancellationToken cancellation = default);

    /// <summary>
    /// Makes the store under the overlay what the overlay shows: deleted things are deleted, then folders and files are written, and the overlay is empty afterwards. It is not atomic: a failure part way leaves what was done, and the overlay as it was, so it can be applied again.
    /// </summary>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the changes are applied.</returns>
    Task Apply(CancellationToken cancellation = default);

    /// <summary>
    /// Throws away every change, so the overlay shows the store under it again. It waits for an apply that is in progress.
    /// </summary>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>A task that completes once the changes are gone.</returns>
    Task Discard(CancellationToken cancellation = default);
}
