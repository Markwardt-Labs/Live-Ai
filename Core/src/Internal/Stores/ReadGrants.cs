namespace Markwardt.LiveAi;

/// <summary>
/// The folders outside the workspace that the AI may read and not write, as they are at this moment. The stores of the file tools look here on every call, so a folder counts from the moment it is granted and no longer once the grant is ended, also while the AI works.
/// </summary>
/// <param name="comparison">How paths are compared, or <see langword="null" /> for the way the file system of this platform usually does.</param>
internal sealed class ReadGrants(StringComparison? comparison = null)
{
    // The grants refuse writes, so a spelling in another case must not slip past them where it names the same file.
    private readonly StringComparison comparing = comparison ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private readonly Lock gate = new();

    private readonly List<Grant> grants = [];

    /// <summary>
    /// Gets a value indicating whether nothing is granted.
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            lock (gate)
            {
                return grants.Count == 0;
            }
        }
    }

    /// <summary>
    /// Grants a folder.
    /// </summary>
    /// <param name="folder">The full path of the folder, with links resolved.</param>
    /// <returns>Ends the grant when disposed.</returns>
    public IDisposable Add(string folder)
    {
        Grant grant = new(this, Path.TrimEndingDirectorySeparator(folder));

        lock (gate)
        {
            grants.Add(grant);
        }

        return grant;
    }

    /// <summary>
    /// Finds the granted folder a path lies in.
    /// </summary>
    /// <param name="path">A full path with links resolved.</param>
    /// <returns>The folder, which may be the path itself, or <see langword="null" /> when the path is in none.</returns>
    public string? Containing(string path)
    {
        lock (gate)
        {
            return grants.Select(grant => grant.Folder).FirstOrDefault(folder => Under(path, folder));
        }
    }

    /// <summary>
    /// Tells whether a granted folder lies under a path, which removing or moving the path would take along.
    /// </summary>
    /// <param name="path">A full path with links resolved.</param>
    /// <returns><see langword="true" /> if one does.</returns>
    public bool Holds(string path)
    {
        lock (gate)
        {
            return grants.Any(grant => Under(grant.Folder, path));
        }
    }

    private bool Under(string path, string folder)
        => path.Equals(folder, comparing) || path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, comparing);

    private sealed class Grant(ReadGrants owner, string folder) : IDisposable
    {
        public string Folder => folder;

        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.grants.Remove(this);
            }
        }
    }
}
