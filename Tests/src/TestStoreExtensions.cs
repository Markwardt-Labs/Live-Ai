namespace Markwardt.LiveAi.Tests;

public static class TestStoreExtensions
{
    public static async Task<IStore> Memory(IReadOnlyDictionary<string, string> files)
    {
        IStore store = IStore.FromMemory();

        foreach ((string path, string text) in files)
        {
            await store.WriteText(path, text);
        }

        return store;
    }

    extension(IStore store)
    {
        public async Task WriteFrom(string path, Stream source)
        {
            await using Stream target = await store.Open(path, StoreOpenMode.Replace);
            await source.CopyToAsync(target);
        }

        public async Task WriteBytes(string path, byte[] bytes)
        {
            using MemoryStream source = new(bytes);
            await store.WriteFrom(path, source);
        }

        public async Task<byte[]> ReadBytes(string path)
        {
            await using Stream read = await store.Open(path, StoreOpenMode.Read);
            using MemoryStream copy = new();
            await read.CopyToAsync(copy);
            return copy.ToArray();
        }
    }
}
