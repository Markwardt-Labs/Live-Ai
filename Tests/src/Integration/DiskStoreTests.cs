namespace Markwardt.LiveAi.Tests.Integration;

public sealed class DiskStoreTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly TempFolder outside = new();

    private readonly DiskStore disk;

    public DiskStoreTests()
        => disk = new DiskStore(temp.Path);

    public void Dispose()
    {
        temp.Dispose();
        outside.Dispose();
    }

    [Fact]
    public async Task Write_CreatesFoldersAndRead_ReturnsTheText()
    {
        await disk.WriteText("a/b/file.txt", "héllo\nworld", CancellationToken.None);

        Assert.Equal("héllo\nworld", await disk.ReadText("a/b/file.txt", CancellationToken.None));
        Assert.Equal("héllo\nworld", await File.ReadAllTextAsync(temp.Combine("a/b/file.txt")));
    }

    [Fact]
    public async Task Write_ReplacesTheContent()
    {
        await disk.WriteText("file.txt", "long long long", CancellationToken.None);
        await disk.WriteText("file.txt", "short", CancellationToken.None);

        Assert.Equal("short", await disk.ReadText("file.txt", CancellationToken.None));
    }

    [Fact]
    public async Task List_ReturnsFilesAndFolders()
    {
        await disk.WriteText("one.txt", "1", CancellationToken.None);
        await disk.WriteText("sub/two.txt", "2", CancellationToken.None);

        List<StoreEntry> entries = await disk.List(string.Empty).ToListAsync();

        Assert.Equal([new StoreEntry("one.txt", false), new StoreEntry("sub", true)], entries.OrderBy(entry => entry.Path));
        Assert.Equal([new StoreEntry("two.txt", false)], await disk.List("sub").ToListAsync());
    }

    [Fact]
    public async Task Read_WhenMissing_SaysSo()
    {
        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() => disk.ReadText("nope.txt", CancellationToken.None));
        DirectoryNotFoundException folder = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => disk.List("nope").ToListAsync().AsTask());

        Assert.Equal("The file 'nope.txt' does not exist.", exception.Message);
        Assert.Equal("The folder 'nope' does not exist.", folder.Message);
    }

    [Fact]
    public async Task Write_OverAFolder_Throws()
    {
        Directory.CreateDirectory(temp.Combine("folder"));

        await Assert.ThrowsAsync<IOException>(() => disk.WriteText("folder", "x", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_DeletesFilesAndFoldersAlsoWhenTheyAreNotEmpty()
    {
        await disk.WriteText("dir/file.txt", "x", CancellationToken.None);
        await disk.WriteText("tree/deep/more/file.txt", "x", CancellationToken.None);
        Directory.CreateDirectory(temp.Combine("empty"));

        await disk.Delete("dir/file.txt", CancellationToken.None);
        await disk.Delete("dir", CancellationToken.None);
        await disk.Delete("empty", CancellationToken.None);
        await disk.Delete("tree", CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
        await Assert.ThrowsAsync<FileNotFoundException>(() => disk.Delete("gone", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_And_Move_RefuseTheWorkspaceItself()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.Delete(string.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.Move(string.Empty, "elsewhere", CancellationToken.None));
        Assert.True(Directory.Exists(temp.Path));
    }

    [Fact]
    public async Task Move_MovesFilesAndFoldersAndNeverReplaces()
    {
        await disk.WriteText("a.txt", "A", CancellationToken.None);
        await disk.WriteText("dir/inner.txt", "I", CancellationToken.None);
        await disk.WriteText("taken.txt", "T", CancellationToken.None);

        await disk.Move("a.txt", "new/b.txt", CancellationToken.None);
        await disk.Move("dir", "moved", CancellationToken.None);

        Assert.Equal("A", await disk.ReadText("new/b.txt", CancellationToken.None));
        Assert.Equal("I", await disk.ReadText("moved/inner.txt", CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => disk.Move("new/b.txt", "taken.txt", CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => disk.Move("missing", "x", CancellationToken.None));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    public async Task Paths_ThatLeadOutside_AreRefused(string path)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.ReadText(path, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.WriteText(path, "x", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.List(path).ToListAsync().AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.Delete(path, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.Move("x", path, CancellationToken.None));
    }

    [Fact]
    public async Task Paths_ThroughALinkThatLeadsOutside_AreRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(outside.Combine("secret.txt"), "secret");
        Directory.CreateSymbolicLink(temp.Combine("link"), outside.Path);
        File.CreateSymbolicLink(temp.Combine("file-link.txt"), outside.Combine("secret.txt"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.ReadText("link/secret.txt", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.ReadText("file-link.txt", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.WriteText("link/new.txt", "x", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.List("link").ToListAsync().AsTask());
        Assert.False(File.Exists(outside.Combine("new.txt")));
    }

    [Fact]
    public async Task Paths_ThroughALinkThatStaysInside_Work()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await disk.WriteText("real/file.txt", "inside", CancellationToken.None);
        Directory.CreateSymbolicLink(temp.Combine("alias"), temp.Combine("real"));

        Assert.Equal("inside", await disk.ReadText("alias/file.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Root_ThatIsItselfALink_IsUsedAsItsTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string link = outside.Combine("workspace-link");
        Directory.CreateSymbolicLink(link, temp.Path);
        DiskStore viaLink = new(link);

        await viaLink.WriteText("file.txt", "x", CancellationToken.None);

        Assert.Equal("x", await File.ReadAllTextAsync(temp.Combine("file.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viaLink.ReadText("../x", CancellationToken.None));
    }

    [Fact]
    public async Task Copy_CopiesAFolderTreeOnDisk()
    {
        await disk.WriteText("src/a.txt", "A", CancellationToken.None);
        await disk.WriteText("src/deep/b.txt", "B", CancellationToken.None);

        await disk.Copy("src", "copy");

        Assert.Equal("A", await File.ReadAllTextAsync(temp.Combine("copy/a.txt")));
        Assert.Equal("B", await File.ReadAllTextAsync(temp.Combine("copy/deep/b.txt")));
        await Assert.ThrowsAsync<IOException>(() => disk.Copy("src", "copy"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => disk.Copy("none", "elsewhere"));
    }

    [Fact]
    public async Task Copy_NeverLeavesTheStore()
    {
        await disk.WriteText("a.txt", "A", CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.Copy("a.txt", "../a-copy.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.Copy("../outside.txt", "b.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.IsFolder("../outside"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.GetSize("../outside"));
    }

    [Fact]
    public void Open_RevealsTheFullPathOfTheFolder()
    {
        DiskStore relative = DiskStore.Open(Path.GetRelativePath(Directory.GetCurrentDirectory(), temp.Path));

        Assert.Equal(temp.Path, DiskStore.Open(temp.Path).Folder);
        Assert.Equal(temp.Path, relative.Folder);
    }

    [Fact]
    public async Task Open_MakesAConfinedStoreOfAFolderOnDisk()
    {
        IStore files = DiskStore.Open(temp.Path);

        await files.WriteText("a/b.txt", "text");

        Assert.Equal("text", await File.ReadAllTextAsync(temp.Combine("a/b.txt")));
        Assert.Equal("text", await files.ReadText("a/b.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.ReadText("../outside.txt"));
    }

    [Fact]
    public void FromDisk_AcceptsARelativePathAndRefusesAFolderThatDoesNotExist()
    {
        DirectoryNotFoundException exception = Assert.Throws<DirectoryNotFoundException>(() => DiskStore.Open(temp.Combine("missing")));

        Assert.Contains("does not exist", exception.Message);
        Assert.NotNull(DiskStore.Open(Path.GetRelativePath(Directory.GetCurrentDirectory(), temp.Path)));
    }

    [Fact]
    public async Task Write_And_Read_AreByteForByteForAnyContent()
    {
        byte[] bytes = [0, 255, 10, 13, 0xEF, 0xBB, 0xBF, 128, 0, 1];
        using MemoryStream source = new(bytes);

        await disk.WriteFrom("bin/data.dat", source);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(temp.Combine("bin/data.dat")));
        await using Stream read = await disk.Open("bin/data.dat", StoreOpenMode.Read);
        using MemoryStream copy = new();
        await read.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Write_ReadsTheStreamFromWhereItIsAndLeavesItOpen()
    {
        using MemoryStream source = new([1, 2, 3, 4, 5]);
        source.Position = 2;

        await disk.WriteFrom("tail.dat", source);

        Assert.Equal([3, 4, 5], await File.ReadAllBytesAsync(temp.Combine("tail.dat")));
        Assert.True(source.CanRead);
        Assert.Equal(5, source.Position);
    }

    [Fact]
    public async Task Write_ReplacesLongerContentCompletely()
    {
        await disk.WriteText("a.txt", "a very long first version");
        using MemoryStream source = new([65]);

        await disk.WriteFrom("a.txt", source);

        Assert.Equal("A", await File.ReadAllTextAsync(temp.Combine("a.txt")));
    }

    [Fact]
    public async Task Write_OfAStreamThatFails_Throws()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => disk.WriteFrom("x.dat", new FailingStream()));

    [Fact]
    public async Task Read_OfALargeFile_StreamsItWithoutALimit()
    {
        await using (FileStream big = File.Create(temp.Combine("big.dat")))
        {
            big.SetLength((20L * 1024 * 1024) + 1);
        }

        await using Stream read = await disk.Open("big.dat", StoreOpenMode.Read);

        Assert.Equal((20L * 1024 * 1024) + 1, read.Length);
        byte[] first = new byte[4];
        Assert.Equal(4, await read.ReadAsync(first));
    }

    [Fact]
    public async Task Read_StreamIsReleasedWhenDisposed()
    {
        await disk.WriteText("a.txt", "A");

        await using (Stream read = await disk.Open("a.txt", StoreOpenMode.Read))
        {
            Assert.True(read.CanRead);
        }

        await disk.WriteText("a.txt", "B");
        Assert.Equal("B", await File.ReadAllTextAsync(temp.Combine("a.txt")));
    }

    [Fact]
    public async Task Read_OfAFolderOrAMissingFile_Throws()
    {
        Directory.CreateDirectory(temp.Combine("folder"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => disk.Open("folder", StoreOpenMode.Read));
        await Assert.ThrowsAsync<FileNotFoundException>(() => disk.Open("missing", StoreOpenMode.Read));
    }

    private sealed class FailingStream : Stream
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
            => throw new InvalidOperationException("the source failed");

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task AbsolutePaths_InsideTheFolder_AreTheSameAsTheRelativeOnesInASandbox()
    {
        await File.WriteAllTextAsync(temp.Combine("here.txt"), "inside");

        Assert.Equal("inside", await disk.ReadText(temp.Combine("here.txt"), CancellationToken.None));
        await disk.WriteText(temp.Combine("made/by-absolute.txt"), "x", CancellationToken.None);
        Assert.Equal("x", await File.ReadAllTextAsync(temp.Combine("made/by-absolute.txt")));
        Assert.Equal(["by-absolute.txt"], (await disk.List(temp.Combine("made")).ToListAsync()).Select(entry => entry.Path));
    }

    [Fact]
    public async Task AbsolutePaths_OutsideTheFolder_AreRefusedInASandbox()
    {
        await File.WriteAllTextAsync(outside.Combine("secret.txt"), "secret");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.ReadText(outside.Combine("secret.txt"), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.WriteText(outside.Combine("new.txt"), "x", CancellationToken.None));
        Assert.Throws<UnauthorizedAccessException>(() => disk.List(outside.Path));
        Assert.False(File.Exists(outside.Combine("new.txt")));
    }

    [Fact]
    public async Task WithoutASandbox_AnyPathOnTheMachineIsValidAbsoluteOrRelative()
    {
        DiskStore open = new(temp.Path, false);
        await File.WriteAllTextAsync(outside.Combine("secret.txt"), "secret");
        string relative = Path.GetRelativePath(temp.Path, outside.Combine("secret.txt"));

        Assert.Equal("secret", await open.ReadText(outside.Combine("secret.txt"), CancellationToken.None));
        Assert.Equal("secret", await open.ReadText(relative, CancellationToken.None));
        await open.WriteText(outside.Combine("made/new.txt"), "x", CancellationToken.None);
        Assert.Equal("x", await File.ReadAllTextAsync(outside.Combine("made/new.txt")));
        Assert.Contains(await open.List(outside.Path).ToListAsync(), entry => entry.Path == "secret.txt");
        Assert.True(await open.IsFolder(outside.Path));
        await open.Copy(outside.Combine("secret.txt"), "copied.txt");
        Assert.Equal("secret", await File.ReadAllTextAsync(temp.Combine("copied.txt")));
        await open.Delete(outside.Combine("made"));
        Assert.False(Directory.Exists(outside.Combine("made")));
    }

    [Fact]
    public async Task WithoutASandbox_RelativePathsStillStartAtTheFolder()
    {
        DiskStore open = new(temp.Path, false);

        await open.WriteText("note.txt", "x", CancellationToken.None);

        Assert.True(File.Exists(temp.Combine("note.txt")));
    }

    [Fact]
    public async Task TheRootOfTheMachine_CanNeitherBeDeletedMovedNorCopiedOver()
    {
        DiskStore open = new(temp.Path, false);
        string root = Path.GetPathRoot(temp.Path)!;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => open.Delete(root));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => open.Move(root, temp.Combine("moved")));
        await Assert.ThrowsAsync<ArgumentException>(() => open.Copy("x", root));
    }
}
