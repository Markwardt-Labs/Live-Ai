namespace Markwardt.LiveAi;

/// <summary>
/// A store of a folder on disk, as <c>IStore.FromDisk</c> makes it. It reveals the folder it is rooted at.
/// </summary>
public interface IDiskStore : IStore
{
    /// <summary>
    /// Gets the full path of the folder the store is rooted at, which relative paths are relative to.
    /// </summary>
    string Path { get; }

    /// <summary>
    /// Gets a value indicating whether the store is a sandbox: only paths that land inside the folder are accepted, relative or absolute, also through a link. When it is not, every path on this machine is accepted.
    /// </summary>
    bool IsSandboxed { get; }
}
