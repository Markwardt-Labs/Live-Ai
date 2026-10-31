namespace Markwardt.LiveAi.Tests;

public sealed class FakeStore(Dictionary<string, string>? seed = null) : IStore
{
    public Dictionary<string, string> Files { get; } = new(seed ?? []);

    public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
    {
        string prefix = directory.Length == 0 ? string.Empty : $"{directory}/";
        IEnumerable<string> rests = Files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).Select(path => path[prefix.Length..]).ToList();

        if (recursive)
        {
            return rests.SelectMany(rest => rest.Split('/').Select((_, index) => string.Join('/', rest.Split('/').Take(index + 1))).Select(entry => new StoreEntry(entry, entry != rest))).Distinct().ToAsyncEnumerable();
        }

        return rests.Select(rest => rest.Contains('/') ? new StoreEntry(rest[..rest.IndexOf('/')], true) : new StoreEntry(rest, false)).Distinct().ToAsyncEnumerable();
    }

    public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
    {
        bool exists = Files.TryGetValue(path, out string? text);

        if (mode is StoreOpenMode.Read or StoreOpenMode.Open && !exists)
        {
            throw new FileNotFoundException($"No such file: {path}");
        }

        if (mode == StoreOpenMode.Create && exists)
        {
            throw new IOException($"Already exists: {path}");
        }

        if (mode is StoreOpenMode.Create or StoreOpenMode.Replace)
        {
            Files[path] = string.Empty;
        }

        byte[] bytes = mode is StoreOpenMode.Read or StoreOpenMode.Open or StoreOpenMode.Append ? Encoding.UTF8.GetBytes(text ?? string.Empty) : [];
        return Task.FromResult<Stream>(mode == StoreOpenMode.Read ? new MemoryStream(bytes, false) : new CommitStream(bytes, mode == StoreOpenMode.Append, committed => Files[path] = Encoding.UTF8.GetString(committed)));
    }

    public Task CreateFolder(string path, CancellationToken cancellation = default)
        => Task.CompletedTask;

    public Task Delete(string path, CancellationToken cancellation = default)
    {
        List<string> inside = Files.Keys.Where(key => key == path || key.StartsWith($"{path}/", StringComparison.Ordinal)).ToList();

        if (inside.Count == 0)
        {
            throw new FileNotFoundException($"No such file: {path}");
        }

        inside.ForEach(key => Files.Remove(key));
        return Task.CompletedTask;
    }

    public Task Move(string from, string to, CancellationToken cancellation = default)
    {
        Files[to] = Files[from];
        Files.Remove(from);
        return Task.CompletedTask;
    }
}
