namespace Markwardt.LiveAi.Tests;

/// <summary>
/// Makes storage pools and tiered stores for tests, with the files of the disk folder in a folder the test owns.
/// </summary>
internal static class TestStorage
{
    /// <summary>
    /// Gets the kinds of store a contract test runs against besides the disk and memory stores: a tiered store with plenty of memory and one that keeps everything in files.
    /// </summary>
    public static IReadOnlyList<string> Kinds { get; } = ["disk", "memory", "tiered", "spilled"];

    /// <summary>
    /// Makes a pool.
    /// </summary>
    /// <param name="folder">The folder of the session on disk, or <see langword="null" /> for no disk.</param>
    /// <param name="memoryLimit">How many bytes stay in memory.</param>
    /// <param name="fileLimit">The most bytes a file may have.</param>
    /// <param name="time">The clock, or <see langword="null" /> for the system's.</param>
    /// <param name="retention">How long unused files in an expiring store are kept.</param>
    /// <returns>The pool.</returns>
    public static StoragePool Pool(string? folder, long memoryLimit = 64L * 1024 * 1024, long fileLimit = 256L * 1024 * 1024, TimeProvider? time = null, TimeSpan? retention = null)
        => new(time ?? TimeProvider.System, new PromptStorageSettings { MemoryLimit = memoryLimit, FileLimit = fileLimit, Retention = retention ?? TimeSpan.FromMinutes(30) }, folder);

    /// <summary>
    /// Makes a store of a kind.
    /// </summary>
    /// <param name="kind">The kind, one of <see cref="Kinds" />.</param>
    /// <param name="folder">The folder the disk store works in, or the one the pool keeps its files in.</param>
    /// <param name="pools">The pools made so far by kind, which a store of a kind shares, so that stores of one kind can move files between them. The caller disposes them.</param>
    /// <returns>The store.</returns>
    public static IStore Make(string kind, string folder, Dictionary<string, StoragePool> pools)
    {
        switch (kind)
        {
            case "disk":
                return DiskStore.Open(folder);
            case "memory":
                return new MemoryStore();
            default:
                if (!pools.TryGetValue(kind, out StoragePool? pool))
                {
                    pools[kind] = pool = Pool(Path.Combine(folder, "pool"), kind == "spilled" ? 0 : 64L * 1024 * 1024);
                }

                return pool.Create("session", false);
        }
    }
}
