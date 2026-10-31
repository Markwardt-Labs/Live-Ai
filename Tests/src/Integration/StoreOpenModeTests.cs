namespace Markwardt.LiveAi.Tests.Integration;

public sealed class StoreOpenModeTests : IDisposable
{
    private readonly TempFolder temp = new();

    public static TheoryData<string> Kinds => ["disk", "memory"];

    public void Dispose()
        => temp.Dispose();

    private IStore Make(string kind)
        => kind == "disk" ? IStore.FromDisk(temp.Path) : IStore.FromMemory();

    private async Task Put(IStore store, string path, string text)
    {
        await using Stream stream = await store.Open(path, StoreOpenMode.Replace);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private async Task Add(IStore store, string path, string text, StoreOpenMode mode)
    {
        await using Stream stream = await store.Open(path, mode);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Read_OpensAnExistingFileForReadingOnly(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "a.txt", "hello");

        await using Stream stream = await store.Open("a.txt", StoreOpenMode.Read);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), await ReadAll(stream));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Read_And_Open_OfAFileThatDoesNotExistOrOfAFolder_Fail(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "dir/inner.txt", "x");

        foreach (StoreOpenMode mode in new[] { StoreOpenMode.Read, StoreOpenMode.Open })
        {
            FileNotFoundException missing = await Assert.ThrowsAsync<FileNotFoundException>(() => store.Open("nope.txt", mode));
            Assert.Equal("The file 'nope.txt' does not exist.", missing.Message);
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.Open("dir", mode));
        }

        Assert.DoesNotContain(await store.List(string.Empty).ToListAsync(), entry => entry.Path == "nope.txt");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Create_MakesANewFileAndItsFolders(string kind)
    {
        IStore store = Make(kind);

        await Add(store, "new/er/file.txt", "created", StoreOpenMode.Create);

        Assert.Equal("created", await store.ReadText("new/er/file.txt"));
        await using Stream stream = await store.Open("other.txt", StoreOpenMode.Create);
        Assert.True(stream.CanWrite);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Create_WhenTheFileOrFolderExists_FailsAndReplacesNothing(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "a.txt", "original");
        await Put(store, "dir/inner.txt", "x");

        IOException file = await Assert.ThrowsAsync<IOException>(() => store.Open("a.txt", StoreOpenMode.Create));
        await Assert.ThrowsAsync<IOException>(() => store.Open("dir", StoreOpenMode.Create));

        Assert.Contains("a.txt", file.Message);
        Assert.Equal("original", await store.ReadText("a.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Replace_DiscardsTheOldContentAndMakesTheFileIfMissing(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "a.txt", "a long first version");

        await Add(store, "a.txt", "short", StoreOpenMode.Replace);
        await Add(store, "fresh/b.txt", "new", StoreOpenMode.Replace);

        Assert.Equal("short", await store.ReadText("a.txt"));
        Assert.Equal("new", await store.ReadText("fresh/b.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Replace_WithoutWritingAnythingEmptiesTheFile(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "a.txt", "content");

        await using (await store.Open("a.txt", StoreOpenMode.Replace))
        {
        }

        Assert.Equal(string.Empty, await store.ReadText("a.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Append_AddsToTheEndAndMakesTheFileIfMissing(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "log.txt", "one\n");

        await Add(store, "log.txt", "two\n", StoreOpenMode.Append);
        await Add(store, "log.txt", "three\n", StoreOpenMode.Append);
        await Add(store, "fresh/new.txt", "first", StoreOpenMode.Append);

        Assert.Equal("one\ntwo\nthree\n", await store.ReadText("log.txt"));
        Assert.Equal("first", await store.ReadText("fresh/new.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Open_ReadsAndWritesInPlaceKeepingTheRest(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "a.txt", "0123456789");

        await using (Stream stream = await store.Open("a.txt", StoreOpenMode.Open))
        {
            Assert.True(stream.CanRead);
            Assert.True(stream.CanWrite);
            Assert.Equal(0, stream.Position);
            byte[] first = new byte[3];
            Assert.Equal(3, await stream.ReadAsync(first));
            Assert.Equal("012", Encoding.UTF8.GetString(first));
            await stream.WriteAsync("XY"u8.ToArray());
        }

        Assert.Equal("012XY56789", await store.ReadText("a.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task WritesAreStoredOnceTheStreamIsDisposed(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "a.txt", "before");

        Stream stream = await store.Open("a.txt", StoreOpenMode.Replace);
        await stream.WriteAsync("after"u8.ToArray());
        await stream.DisposeAsync();

        Assert.Equal("after", await store.ReadText("a.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryWriteMode_OnAFolder_Fails(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "dir/inner.txt", "x");

        foreach (StoreOpenMode mode in new[] { StoreOpenMode.Create, StoreOpenMode.Replace, StoreOpenMode.Append })
        {
            IOException exception = await Assert.ThrowsAsync<IOException>(() => store.Open("dir", mode));
            Assert.Contains("dir", exception.Message);
        }

        Assert.Equal("x", await store.ReadText("dir/inner.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryWriteMode_ThroughAFile_Fails(string kind)
    {
        IStore store = Make(kind);
        await Put(store, "a.txt", "file");

        foreach (StoreOpenMode mode in new[] { StoreOpenMode.Create, StoreOpenMode.Replace, StoreOpenMode.Append })
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.Open("a.txt/inside.txt", mode));
        }

        Assert.Equal("file", await store.ReadText("a.txt"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AnUndefinedMode_IsRefused(string kind)
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => Make(kind).Open("a.txt", (StoreOpenMode)99));

    [Fact]
    public async Task Memory_Flush_StoresWhatWasWrittenBeforeTheStreamIsDisposed()
    {
        IStore store = IStore.FromMemory();

        await using Stream stream = await store.Open("a.txt", StoreOpenMode.Replace);
        await stream.WriteAsync("flushed"u8.ToArray());
        await stream.FlushAsync();

        Assert.Equal("flushed", await store.ReadText("a.txt"));
    }

    [Fact]
    public async Task Memory_OpeningAFileForWritingMakesItExistAtOnce()
    {
        IStore store = IStore.FromMemory();

        await using Stream stream = await store.Open("a.txt", StoreOpenMode.Create);

        Assert.Contains(await store.List(string.Empty).ToListAsync(), entry => entry.Path == "a.txt");
        await Assert.ThrowsAsync<IOException>(() => store.Open("a.txt", StoreOpenMode.Create));
    }

    [Fact]
    public async Task Disk_TheStreamsAreExactlyAsCapableAsTheModeSays()
    {
        IStore store = IStore.FromDisk(temp.Path);
        await Put(store, "a.txt", "x");

        foreach ((StoreOpenMode mode, bool canRead, bool canWrite) in new[] { (StoreOpenMode.Read, true, false), (StoreOpenMode.Open, true, true), (StoreOpenMode.Replace, false, true), (StoreOpenMode.Append, false, true) })
        {
            await using Stream stream = await store.Open("a.txt", mode);

            Assert.Equal(canRead, stream.CanRead);
            Assert.Equal(canWrite, stream.CanWrite);
        }
    }

    private async Task<byte[]> ReadAll(Stream stream)
    {
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
