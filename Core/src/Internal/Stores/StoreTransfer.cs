namespace Markwardt.LiveAi;

/// <summary>
/// A store that can move a file or folder into another store of its own kind by changing its place, instead of copying it and deleting the original.
/// </summary>
internal interface IStoreTransfer
{
    /// <summary>
    /// Moves a file or a folder with everything in it into another store, if that store is of a kind this one can move into without copying.
    /// </summary>
    /// <param name="from">The file or folder in this store.</param>
    /// <param name="target">The store it goes to.</param>
    /// <param name="to">Where it goes in <paramref name="target" />; nothing may be there.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns><see langword="true" /> when it was moved; <see langword="false" /> when this store cannot move into <paramref name="target" /> without copying, in which case nothing has changed.</returns>
    /// <exception cref="FileNotFoundException"><paramref name="from" /> does not exist.</exception>
    /// <exception cref="IOException"><paramref name="to" /> already exists.</exception>
    Task<bool> TryMoveTo(string from, IStore target, string to, CancellationToken cancellation = default);
}
