namespace Markwardt.LiveAi;

/// <summary>
/// The kinds of access that <see cref="StoreExtensions.Audited(IStore, Func{StoreAuditEntry, CancellationToken, Task})" /> reports.
/// </summary>
public enum StoreOperation
{
    /// <summary>
    /// A folder was listed.
    /// </summary>
    List,

    /// <summary>
    /// A file was opened for reading only.
    /// </summary>
    Read,

    /// <summary>
    /// A file was opened in a way that lets it be changed.
    /// </summary>
    Write,

    /// <summary>
    /// A folder was made.
    /// </summary>
    CreateFolder,

    /// <summary>
    /// A file or folder was deleted.
    /// </summary>
    Delete,

    /// <summary>
    /// A file or folder was moved or renamed.
    /// </summary>
    Move,

    /// <summary>
    /// A file or folder was copied natively by the store (a copy made through streams is reported as the reads and writes it consists of).
    /// </summary>
    Copy
}
