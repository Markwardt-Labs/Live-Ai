namespace Markwardt.LiveAi;

/// <summary>
/// What a change recorded by an <see cref="IOverlayStore" /> does to the store under it.
/// </summary>
public enum StoreChangeKind
{
    /// <summary>
    /// Something is there that was not.
    /// </summary>
    Added,

    /// <summary>
    /// A file is there with other content.
    /// </summary>
    Modified,

    /// <summary>
    /// Something that was there is gone.
    /// </summary>
    Deleted
}
