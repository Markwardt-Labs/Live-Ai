namespace Markwardt.LiveAi.Tests.Unit;

public sealed class StoreExtensionsTests
{
    private readonly FakeStore files = new(new Dictionary<string, string>
    {
        ["root.txt"] = "root",
        ["src/Config.cs"] = "config",
        ["src/deep/Worker.cs"] = "worker",
        ["src/deep/er/Leaf.cs"] = "leaf",
        ["other/Thing.cs"] = "thing"
    });

    private IStore Store => files;

    [Fact]
    public async Task Copy_CopiesAFileAndLeavesTheOriginal()
    {
        await Store.Copy("root.txt", "copy/root-copy.txt");

        Assert.Equal("root", files.Files["copy/root-copy.txt"]);
        Assert.Equal("root", files.Files["root.txt"]);
    }

    [Fact]
    public async Task Copy_CopiesAFileWithinAFolder()
    {
        await Store.Copy("src/Config.cs", "src/Config.backup");

        Assert.Equal("config", files.Files["src/Config.backup"]);
    }

    [Fact]
    public async Task Copy_CopiesAFolderWithEverythingInIt()
    {
        await Store.Copy("src", "backup/src");

        Assert.Equal("config", files.Files["backup/src/Config.cs"]);
        Assert.Equal("worker", files.Files["backup/src/deep/Worker.cs"]);
        Assert.Equal("leaf", files.Files["backup/src/deep/er/Leaf.cs"]);
        Assert.Equal(8, files.Files.Count - 0);
        Assert.Equal("config", files.Files["src/Config.cs"]);
    }

    [Fact]
    public async Task Copy_WhenTheSourceIsMissing_Throws()
    {
        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() => Store.Copy("nope.txt", "x.txt"));

        Assert.Equal("'nope.txt' does not exist.", exception.Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Store.Copy("missing/folder/file.txt", "x.txt"));
        Assert.DoesNotContain("x.txt", files.Files.Keys);
    }

    [Theory]
    [InlineData("root.txt", "src/Config.cs")]
    [InlineData("root.txt", "src")]
    [InlineData("src", "other")]
    [InlineData("src/deep", "src/Config.cs")]
    public async Task Copy_WhenTheDestinationExists_ThrowsAndChangesNothing(string from, string to)
    {
        Dictionary<string, string> before = new(files.Files);

        IOException exception = await Assert.ThrowsAsync<IOException>(() => Store.Copy(from, to));

        Assert.Equal($"'{to}' already exists.", exception.Message);
        Assert.Equal(before, files.Files);
    }

    [Fact]
    public async Task Copy_OfAFolderIntoItself_Throws()
    {
        Dictionary<string, string> before = new(files.Files);

        IOException exception = await Assert.ThrowsAsync<IOException>(() => Store.Copy("src", "src/inside/src"));

        Assert.Contains("into itself", exception.Message);
        Assert.Equal(before, files.Files);
    }

    [Fact]
    public async Task Copy_OfAFolderToASiblingWithTheSamePrefix_IsNotIntoItself()
    {
        await Store.Copy("src", "src2");

        Assert.Equal("config", files.Files["src2/Config.cs"]);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("root.txt", "")]
    public async Task Copy_OfTheWorkspaceItself_Throws(string from, string to)
        => await Assert.ThrowsAsync<ArgumentException>(() => Store.Copy(from, to));

    [Fact]
    public async Task Copy_PassesTheCancellationToken()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        Mock<IStore> mock = new() { CallBase = true };
        mock.Setup(system => system.List(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns((string _, bool _, CancellationToken token) => Throwing(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mock.Object.Copy("a.txt", "b.txt", cancellation.Token));
    }

    private async IAsyncEnumerable<StoreEntry> Throwing([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new StoreEntry("a.txt", false);
    }

    [Fact]
    public async Task WriteText_WritesUtf8WithoutAByteOrderMarkAndReadTextReadsItBack()
    {
        IStore memory = IStore.FromMemory();

        await memory.WriteText("a/b.txt", "héllo wörld ✓\r\nnext");

        await using Stream read = await memory.Open("a/b.txt", StoreOpenMode.Read);
        using MemoryStream copy = new();
        await read.CopyToAsync(copy);
        Assert.Equal(Encoding.UTF8.GetBytes("héllo wörld ✓\r\nnext"), copy.ToArray());
        Assert.Equal("héllo wörld ✓\r\nnext", await memory.ReadText("a/b.txt"));
    }

    [Fact]
    public async Task WriteText_OfNothing_CreatesAnEmptyFile()
    {
        IStore memory = IStore.FromMemory();

        await memory.WriteText("empty.txt", string.Empty);

        Assert.Equal(string.Empty, await memory.ReadText("empty.txt"));
        Assert.Contains(await memory.List(string.Empty).ToListAsync(), entry => entry.Path == "empty.txt");
    }

    [Fact]
    public async Task ReadText_DecodesTheEncodingOfAByteOrderMark()
    {
        IStore memory = IStore.FromMemory();
        foreach ((string name, byte[] bytes) in new[] { ("utf8.txt", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("bom ✓")).ToArray()), ("utf16.txt", new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("wide ✓")).ToArray()) })
        {
            using MemoryStream source = new(bytes);
            await memory.WriteFrom(name, source);
        }

        Assert.Equal("bom ✓", await memory.ReadText("utf8.txt"));
        Assert.Equal("wide ✓", await memory.ReadText("utf16.txt"));
    }

    [Fact]
    public async Task ReadText_OfBytesThatAreNotUtf8_ReplacesWhatItCannotDecode()
    {
        IStore memory = IStore.FromMemory();
        using MemoryStream source = new([0x61, 0xFF, 0x62]);
        await memory.WriteFrom("bad.dat", source);

        Assert.Equal("a\uFFFDb", await memory.ReadText("bad.dat"));
    }

    [Fact]
    public async Task ReadText_OfAMissingFile_Throws()
        => await Assert.ThrowsAsync<FileNotFoundException>(() => IStore.FromMemory().ReadText("nope.txt"));

    [Fact]
    public async Task ReadText_And_WriteText_PassTheCancellationToken()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        IStore memory = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["a.txt"] = "A" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memory.ReadText("a.txt", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memory.WriteText("b.txt", "B", cancellation.Token));
    }

    [Fact]
    public async Task Copy_CopiesBinaryFilesByteForByteAcrossStores()
    {
        byte[] bytes = [0, 255, 10, 13, 0xEF, 0xBB, 0xBF, 128, 0, 1, 0xFF, 0xFE];
        IStore from = IStore.FromMemory();
        IStore to = IStore.FromMemory();
        using MemoryStream source = new(bytes);
        await from.WriteFrom("tree/deep/blob.dat", source);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["from"] = from, ["to"] = to });

        await mounted.Copy("from/tree", "to/tree-copy");

        await using Stream read = await to.Open("tree-copy/deep/blob.dat", StoreOpenMode.Read);
        using MemoryStream copy = new();
        await read.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Copy_WhenReadingTheSourceFailsHalfWay_RemovesTheHalfWrittenDestination()
    {
        IStore inner = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["big.dat"] = new string('x', 100000), ["other.txt"] = "kept" });
        IStore failing = new FailingReadStore(inner, "big.dat");

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Copy("big.dat", "copy.dat"));

        Assert.DoesNotContain(await inner.List(string.Empty).ToListAsync(), entry => entry.Path == "copy.dat");
        Assert.Equal("kept", await inner.ReadText("other.txt"));
    }

    [Fact]
    public async Task Copy_WhenTheDestinationAppearsAfterTheCheck_FailsAndNeverTouchesIt()
    {
        IStore inner = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["a.txt"] = "A", ["taken.txt"] = "someone else's" });
        IStore blind = new HidingStore(inner, "taken.txt");

        await Assert.ThrowsAsync<IOException>(() => blind.Copy("a.txt", "taken.txt"));

        Assert.Equal("someone else's", await inner.ReadText("taken.txt"));
    }

    private sealed class FailingReadStore(IStore inner, string failing) : IStore
    {
        public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
            => inner.List(directory, recursive, cancellation);

        public async Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
            => path == failing && mode == StoreOpenMode.Read ? new HalfStream(await inner.Open(path, mode, cancellation)) : await inner.Open(path, mode, cancellation);

        public Task CreateFolder(string path, CancellationToken cancellation = default)
            => inner.CreateFolder(path, cancellation);

        public Task Delete(string path, CancellationToken cancellation = default)
            => inner.Delete(path, cancellation);

        public Task Move(string from, string to, CancellationToken cancellation = default)
            => inner.Move(from, to, cancellation);
    }

    private sealed class HidingStore(IStore inner, string hidden) : IStore
    {
        public IAsyncEnumerable<StoreEntry> List(string directory, bool recursive = false, CancellationToken cancellation = default)
            => inner.List(directory, recursive, cancellation).Where(entry => entry.Path != hidden);

        public Task<Stream> Open(string path, StoreOpenMode mode, CancellationToken cancellation = default)
            => inner.Open(path, mode, cancellation);

        public Task CreateFolder(string path, CancellationToken cancellation = default)
            => inner.CreateFolder(path, cancellation);

        public Task Delete(string path, CancellationToken cancellation = default)
            => inner.Delete(path, cancellation);

        public Task Move(string from, string to, CancellationToken cancellation = default)
            => inner.Move(from, to, cancellation);
    }

    private sealed class HalfStream(Stream inner) : Stream
    {
        private int total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (total > 0)
            {
                throw new InvalidOperationException("the source failed half way");
            }

            int read = inner.Read(buffer, offset, Math.Min(count, 1000));
            total += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
