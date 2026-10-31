namespace Markwardt.LiveAi;

/// <summary>
/// How <see cref="IStore.Open" /> opens a file.
/// </summary>
public enum StoreOpenMode
{
    /// <summary>
    /// Makes a new file for writing, and its folders. Fails if anything is already at the path, so nothing is ever replaced.
    /// </summary>
    Create,

    /// <summary>
    /// Opens a file that exists for reading and writing, with its content kept and the position at its start. Fails if there is no such file.
    /// </summary>
    Open,

    /// <summary>
    /// Opens a file for writing and throws away its content, making the file (and its folders) if it is not there.
    /// </summary>
    Replace,

    /// <summary>
    /// Opens a file for writing at its end, making the file (and its folders) if it is not there.
    /// </summary>
    Append,

    /// <summary>
    /// Opens a file that exists for reading only. Fails if there is no such file.
    /// </summary>
    Read
}
