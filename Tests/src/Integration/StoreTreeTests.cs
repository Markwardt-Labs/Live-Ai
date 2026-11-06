namespace Markwardt.LiveAi.Tests.Integration;

public sealed class StoreTreeTests : IDisposable
{
    public static TheoryData<string, string> Pairs => new()
    {
        { "disk", "disk" },
        { "disk", "memory" },
        { "memory", "disk" },
        { "memory", "memory" }
    };

    private readonly TempFolder source = new();

    private readonly TempFolder target = new();

    public void Dispose()
    {
        source.Dispose();
        target.Dispose();
    }

    private IStore Make(string kind, TempFolder folder)
        => kind == "disk" ? DiskStore.Open(folder.Path) : new MemoryStore();

    private async Task<IStore> Tree(string kind)
    {
        IStore store = Make(kind, source);
        await store.WriteText("tree/a.txt", "A");
        await store.WriteText("tree/deep/b.txt", "B");
        await store.CreateFolder("tree/empty");
        await store.CreateFolder("tree/deep/also/empty");
        return store;
    }

    private async Task<List<string>> Everything(IStore store, string path = "")
    {
        List<string> found = [];

        await foreach (StoreEntry entry in store.List(path))
        {
            string full = path.Length == 0 ? entry.Path : $"{path}/{entry.Path}";
            found.Add(entry.IsDirectory ? $"{full}/" : full);

            if (entry.IsDirectory)
            {
                found.AddRange(await Everything(store, full));
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Copy_OfAFolder_CopiesEverythingIncludingEmptyFolders(string from, string to)
    {
        IStore origin = await Tree(from);
        IStore destination = Make(to, target);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["a"] = origin, ["b"] = destination });

        await mounted.Copy("a/tree", "b/copy");

        Assert.Equal(["copy/", "copy/a.txt", "copy/deep/", "copy/deep/also/", "copy/deep/also/empty/", "copy/deep/b.txt", "copy/empty/"], await Everything(destination));
        Assert.Equal("B", await destination.ReadText("copy/deep/b.txt"));
        Assert.Equal(7, (await Everything(origin)).Count);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Move_OfAFolder_MovesEverythingIncludingEmptyFoldersAndLeavesNothingBehind(string from, string to)
    {
        IStore origin = await Tree(from);
        IStore destination = Make(to, target);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["a"] = origin, ["b"] = destination });

        await mounted.Move("a/tree", "b/moved");

        Assert.Equal(["moved/", "moved/a.txt", "moved/deep/", "moved/deep/also/", "moved/deep/also/empty/", "moved/deep/b.txt", "moved/empty/"], await Everything(destination));
        Assert.Equal("A", await destination.ReadText("moved/a.txt"));
        Assert.Empty(await Everything(origin));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task Copy_And_Move_WithinOneStore_KeepEmptyFolders(string kind)
    {
        IStore store = await Tree(kind);

        await store.Copy("tree", "copy");
        await store.Move("tree", "moved");

        List<string> all = await Everything(store);
        Assert.Equal(14, all.Count);
        Assert.Contains("copy/deep/also/empty/", all);
        Assert.Contains("moved/deep/also/empty/", all);
        Assert.DoesNotContain("tree/", all);
    }

    [Fact]
    public async Task Move_BetweenTwoDiskStores_RelocatesTheFilesInsteadOfCopyingThem()
    {
        IStore origin = await Tree("disk");
        IStore destination = Make("disk", target);
        DateTime stamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source.Combine("tree/a.txt"), stamp);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["a"] = origin, ["b"] = destination });

        await mounted.Move("a/tree", "b/moved");

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(target.Combine("moved/a.txt")));
        Assert.False(Directory.Exists(source.Combine("tree")));
    }

    [Fact]
    public async Task Move_BetweenTwoMemoryStores_TransfersTheTreeAndSharesNothingAfterwards()
    {
        IStore origin = await Tree("memory");
        IStore destination = Make("memory", target);

        Assert.True(await ((IStoreTransfer)origin).TryMoveTo("tree", destination, "moved"));

        Assert.Empty(await Everything(origin));
        Assert.Equal(7, (await Everything(destination)).Count);
    }

    [Fact]
    public async Task TryMoveTo_ReportsFalseAndChangesNothingForStoresItCannotMoveBetween()
    {
        DiskStore disk = new(source.Path);
        IStore memory = Make("memory", target);
        await disk.WriteText("a.txt", "A");
        MemoryStore other = new();
        await other.WriteText("m.txt", "M");

        Assert.False(await disk.TryMoveTo("a.txt", memory, "a.txt"));
        Assert.False(await other.TryMoveTo("m.txt", disk, "m.txt"));
        Assert.False(await other.TryMoveTo("m.txt", other, "n.txt"));

        Assert.Equal("A", await disk.ReadText("a.txt"));
        Assert.Equal("M", await other.ReadText("m.txt"));
        Assert.Empty(await Everything(memory));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task TryMoveTo_ToAnExistingDestinationOrFromAMissingSource_ThrowsAndChangesNothing(string kind)
    {
        IStore first = Make(kind, source);
        IStore second = Make(kind, target);
        await first.WriteText("a.txt", "A");
        await second.WriteText("a.txt", "taken");

        await Assert.ThrowsAsync<IOException>(() => ((IStoreTransfer)first).TryMoveTo("a.txt", second, "a.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ((IStoreTransfer)first).TryMoveTo("missing", second, "deep/new/missing"));

        Assert.Equal("A", await first.ReadText("a.txt"));
        Assert.Equal("taken", await second.ReadText("a.txt"));
        Assert.Equal(["a.txt"], await Everything(second));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task Move_OfAFolderIntoItself_FailsWithoutExposingHostPathsAndChangesNothing(string kind)
    {
        IStore store = await Tree(kind);

        IOException exception = await Assert.ThrowsAsync<IOException>(() => store.Move("tree", "tree/deep/inside"));

        Assert.DoesNotContain(source.Path, exception.Message);
        Assert.Equal(7, (await Everything(store)).Count);
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task Move_OfAMissingSource_DoesNotCreateTheDestinationFolders(string kind)
    {
        IStore store = Make(kind, source);

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.Move("missing", "new/place/missing"));

        Assert.Empty(await Everything(store));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task CreateFolder_MakesTheFolderAndItsParentsAndIsRepeatable(string kind)
    {
        IStore store = Make(kind, source);

        await store.CreateFolder("a/b/c");
        await store.CreateFolder("a/b/c");

        Assert.Equal(["a/", "a/b/", "a/b/c/"], await Everything(store));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task CreateFolder_OverAFileOrThroughOne_Throws(string kind)
    {
        IStore store = Make(kind, source);
        await store.WriteText("file.txt", "x");

        await Assert.ThrowsAsync<IOException>(() => store.CreateFolder("file.txt"));
        await Assert.ThrowsAsync<IOException>(() => store.CreateFolder("file.txt/inner"));

        Assert.Equal(["file.txt"], await Everything(store));
    }

    [Fact]
    public async Task Copy_OfAFolderThatFailsHalfWay_RemovesThePartialDestinationAndKeepsTheSource()
    {
        MemoryStore inner = new();
        await inner.WriteText("tree/a.txt", "A");
        await inner.WriteText("tree/z.txt", "Z");
        IStore broken = new BrokenReadStore(inner, "tree/z.txt");
        IStore destination = Make("memory", target);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["a"] = broken, ["b"] = destination });

        await Assert.ThrowsAsync<InvalidOperationException>(() => mounted.Copy("a/tree", "b/copy"));

        Assert.Empty(await Everything(destination));
        Assert.Equal(["tree/", "tree/a.txt", "tree/z.txt"], await Everything(inner));
    }

    [Fact]
    public async Task Move_ThatFailsToCopy_KeepsTheSourceIntact()
    {
        MemoryStore inner = new();
        await inner.WriteText("tree/a.txt", "A");
        await inner.WriteText("tree/z.txt", "Z");
        IStore broken = new BrokenReadStore(inner, "tree/z.txt");
        IStore destination = Make("disk", target);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["a"] = broken, ["b"] = destination });

        await Assert.ThrowsAsync<InvalidOperationException>(() => mounted.Move("a/tree", "b/moved"));

        Assert.Empty(await Everything(destination));
        Assert.Equal(["tree/", "tree/a.txt", "tree/z.txt"], await Everything(inner));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task IsFolder_IsTrueOnlyForFoldersIncludingTheRoot(string kind)
    {
        IStore store = await Tree(kind);

        Assert.True(await store.IsFolder(string.Empty));
        Assert.True(await store.IsFolder("tree"));
        Assert.True(await store.IsFolder("tree/empty"));
        Assert.False(await store.IsFolder("tree/a.txt"));
        Assert.False(await store.IsFolder("tree/missing"));
        Assert.False(await store.IsFolder("tree/a.txt/inner"));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task GetSize_OfAFileIsItsBytesAndOfAFolderTheSumOfEverythingInIt(string kind)
    {
        IStore store = await Tree(kind);
        await store.WriteText("tree/deep/ü.txt", "üü");

        Assert.Equal(1, await store.GetSize("tree/a.txt"));
        Assert.Equal(4, await store.GetSize("tree/deep/ü.txt"));
        Assert.Equal(5, await store.GetSize("tree/deep"));
        Assert.Equal(6, await store.GetSize("tree"));
        Assert.Equal(6, await store.GetSize(string.Empty));
        Assert.Equal(0, await store.GetSize("tree/empty"));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task GetSize_OfSomethingMissing_Throws(string kind)
    {
        IStore store = await Tree(kind);

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.GetSize("tree/missing"));
    }

    [Fact]
    public async Task IsFolder_And_GetSize_WorkAcrossMounts()
    {
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["a"] = await Tree("memory"), ["b"] = await Tree("disk") });

        Assert.True(await mounted.IsFolder(string.Empty));
        Assert.True(await mounted.IsFolder("a"));
        Assert.False(await mounted.IsFolder("a/tree/a.txt"));
        Assert.Equal(1, await mounted.GetSize("a/tree/deep/b.txt"));
        Assert.Equal(4, await mounted.GetSize(string.Empty));
    }

    [Fact]
    public async Task TheDefaults_OfAStoreThatImplementsOnlyTheFiveOperations_RefusePathsOutsideAndStillWork()
    {
        MemoryStore inner = new();
        await inner.WriteText("dir/a.txt", "abc");
        IStore plain = new PlainStore(inner);

        Assert.True(await plain.IsFolder("dir"));
        Assert.False(await plain.IsFolder("dir/a.txt"));
        Assert.Equal(3, await plain.GetSize("dir"));
        await plain.Copy("dir", "copy");
        Assert.Equal("abc", await inner.ReadText("copy/a.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => plain.IsFolder("../outside"));
        await Assert.ThrowsAsync<ArgumentException>(() => plain.GetSize("../outside"));
        await Assert.ThrowsAsync<ArgumentException>(() => plain.Copy("dir", "../outside"));
    }

    [Fact]
    public async Task GetSize_OfAStreamThatCannotSeek_CountsTheBytes()
    {
        MemoryStore inner = new();
        await inner.WriteText("big.txt", new string('x', 200000));

        Assert.Equal(200000, await ((IStore)new UnseekableStore(inner)).GetSize("big.txt"));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task Copy_Natively_RefusesTheRootAnExistingDestinationAndACopyIntoItself(string kind)
    {
        IStore store = await Tree(kind);

        await Assert.ThrowsAsync<ArgumentException>(() => store.Copy(string.Empty, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Copy("tree", string.Empty));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.Copy("missing", "new/place"));
        await Assert.ThrowsAsync<IOException>(() => store.Copy("tree/a.txt", "tree/deep"));
        IOException inside = await Assert.ThrowsAsync<IOException>(() => store.Copy("tree", "tree/deep/inside"));

        Assert.DoesNotContain(source.Path, inside.Message);
        Assert.Equal(7, (await Everything(store)).Count);
        Assert.Empty(await store.ListFolders("", false).Where(path => path == "new").ToListAsync());
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task Copy_Natively_MakesAnIndependentCopy(string kind)
    {
        IStore store = await Tree(kind);

        await store.Copy("tree", "copy");
        await store.WriteText("copy/a.txt", "changed");
        await store.Delete("tree/deep");

        Assert.Equal("A", await store.ReadText("tree/a.txt"));
        Assert.Equal("changed", await store.ReadText("copy/a.txt"));
        Assert.Equal("B", await store.ReadText("copy/deep/b.txt"));
    }

    [Fact]
    public async Task Copy_OnDisk_OfAFolderWithALinkBackToItself_FailsAndLeavesNoCopy()
    {
        IStore store = await Tree("disk");
        Directory.CreateSymbolicLink(source.Combine("tree/up"), source.Combine("tree"));

        await Assert.ThrowsAsync<IOException>(() => store.Copy("tree", "copy"));

        Assert.False(Directory.Exists(source.Combine("copy")));
    }

    [Fact]
    public async Task Copy_OnDisk_OfALinkOutOfTheFolder_IsRefusedAndLeavesNoCopy()
    {
        IStore store = await Tree("disk");
        using TempFolder outside = new();
        File.WriteAllText(outside.Combine("secret.txt"), "s");
        File.CreateSymbolicLink(source.Combine("tree/leak.txt"), outside.Combine("secret.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Copy("tree", "copy"));

        Assert.False(Directory.Exists(source.Combine("copy")));
    }

    [Fact]
    public async Task Mounted_Copy_WithinOneMountedStorage_IsDoneByThatStore()
    {
        Mock<IStore> mock = new() { CallBase = true };
        mock.Setup(store => store.Copy("a", "b", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["m"] = mock.Object });

        await ((IStore)mounted).Copy("m/a", "m/b");

        mock.Verify(store => store.Copy("a", "b", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(store => store.List(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class PlainStore(IStore inner) : IStore
    {
        public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
            => inner.List(directory, recursive, cancellation);

        public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
            => inner.Open(path, mode, cancellation);

        public Task CreateFolder(string path, CancellationToken cancellation = default)
            => inner.CreateFolder(path, cancellation);

        public Task Delete(string path, CancellationToken cancellation = default)
            => inner.Delete(path, cancellation);

        public Task Move(string from, string to, CancellationToken cancellation = default)
            => inner.Move(from, to, cancellation);
    }

    private sealed class UnseekableStore(IStore inner) : IStore
    {
        public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
            => inner.List(directory, recursive, cancellation);

        public async Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
            => new Forward(await inner.Open(path, mode, cancellation));

        public Task CreateFolder(string path, CancellationToken cancellation = default)
            => inner.CreateFolder(path, cancellation);

        public Task Delete(string path, CancellationToken cancellation = default)
            => inner.Delete(path, cancellation);

        public Task Move(string from, string to, CancellationToken cancellation = default)
            => inner.Move(from, to, cancellation);
    }

    private sealed class Forward(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, Math.Min(count, 1000));

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private static string[] Shown(IEnumerable<StoreEntry> entries)
        => entries.Select(entry => entry.IsDirectory ? $"{entry.Path}/" : entry.Path).Order(StringComparer.Ordinal).ToArray();

    private static string[] Sorted(IEnumerable<string> paths, string suffix)
        => paths.Select(path => path + suffix).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task List_Recursive_ReturnsEverythingBelowWithPathsRelativeToTheFolder(string kind)
    {
        IStore store = await Tree(kind);

        Assert.Equal(["a.txt", "deep/", "deep/also/", "deep/also/empty/", "deep/b.txt", "empty/"], Shown(await store.List("tree", true).ToListAsync()));
        Assert.Equal(["tree/", "tree/a.txt", "tree/deep/", "tree/deep/also/", "tree/deep/also/empty/", "tree/deep/b.txt", "tree/empty/"], Shown(await store.List(string.Empty, true).ToListAsync()));
        Assert.Equal(["a.txt", "deep/", "empty/"], Shown(await store.List("tree").ToListAsync()));
        Assert.Equal(["also/", "also/empty/", "b.txt"], Shown(await store.List("tree/deep", true).ToListAsync()));
        Assert.Empty(await store.List("tree/empty", true).ToListAsync());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.List("nope", true).ToListAsync().AsTask());
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("memory")]
    public async Task ListFiles_And_ListFolders_ReturnPathsAndTakeTheRecursiveOption(string kind)
    {
        IStore store = await Tree(kind);

        Assert.Equal(["a.txt"], Sorted(await store.ListFiles("tree").ToListAsync(), ""));
        Assert.Equal(["a.txt", "deep/b.txt"], Sorted(await store.ListFiles("tree", true).ToListAsync(), ""));
        Assert.Equal(["deep/", "empty/"], Sorted(await store.ListFolders("tree").ToListAsync(), "/"));
        Assert.Equal(["deep/", "deep/also/", "deep/also/empty/", "empty/"], Sorted(await store.ListFolders("tree", true).ToListAsync(), "/"));
    }

    [Fact]
    public async Task List_Recursive_OnDisk_DoesNotFollowLinksOutOfTheFolderOrLoopForever()
    {
        IStore store = await Tree("disk");
        Directory.CreateSymbolicLink(source.Combine("tree/up"), source.Path);
        using TempFolder outside = new();
        File.WriteAllText(outside.Combine("secret.txt"), "s");
        Directory.CreateSymbolicLink(source.Combine("tree/out"), outside.Path);

        List<string> names = Shown(await store.List("tree", true).ToListAsync()).ToList();

        Assert.Contains("out/", names);
        Assert.DoesNotContain(names, name => name.Contains("secret"));
        Assert.True(names.Count < 40);
    }

    [Fact]
    public async Task List_Recursive_AcrossMountsPrefixesEachMount()
    {
        IStore first = await Tree("memory");
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["a"] = first, ["b"] = Make("memory", target) });

        Assert.Equal(["a/", "a/tree/", "a/tree/a.txt", "a/tree/deep/", "a/tree/deep/also/", "a/tree/deep/also/empty/", "a/tree/deep/b.txt", "a/tree/empty/", "b/"], Shown(await mounted.List(string.Empty, true).ToListAsync()));
        Assert.Equal(["tree/", "tree/a.txt", "tree/deep/", "tree/deep/also/", "tree/deep/also/empty/", "tree/deep/b.txt", "tree/empty/"], Shown(await mounted.List("a", true).ToListAsync()));
        Assert.Equal(["a/", "b/"], Sorted(await mounted.ListFolders(string.Empty).ToListAsync(), "/"));
    }

    private sealed class BrokenReadStore(IStore inner, string failing) : IStore
    {
        public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
            => inner.List(directory, recursive, cancellation);

        public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
            => path == failing && mode == StoreOpenMode.Read ? throw new InvalidOperationException("the source failed") : inner.Open(path, mode, cancellation);

        public Task CreateFolder(string path, CancellationToken cancellation = default)
            => inner.CreateFolder(path, cancellation);

        public Task Delete(string path, CancellationToken cancellation = default)
            => inner.Delete(path, cancellation);

        public Task Move(string from, string to, CancellationToken cancellation = default)
            => inner.Move(from, to, cancellation);
    }
}
