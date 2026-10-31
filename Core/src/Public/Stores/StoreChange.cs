namespace Markwardt.LiveAi;

/// <summary>
/// One difference between an <see cref="IOverlayStore" /> and the store under it.
/// </summary>
/// <param name="Path">The path of the file or folder.</param>
/// <param name="IsDirectory">Whether it is a folder; a deleted folder is one change, not one for everything in it.</param>
/// <param name="Kind">What changed.</param>
public sealed record StoreChange(string Path, bool IsDirectory, StoreChangeKind Kind);
