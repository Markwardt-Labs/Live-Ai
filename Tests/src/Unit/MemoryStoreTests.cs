namespace Markwardt.LiveAi.Tests.Unit;

public sealed class MemoryStoreTests : IAsyncLifetime
{
    private IStore store = null!;

    public async Task InitializeAsync()
        => store = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["a.txt"] = "A", ["dir/b.txt"] = "B", ["dir/deep/c.txt"] = "C" });

    public Task DisposeAsync()
        => Task.CompletedTask;

    private async Task<string[]> Names(string directory)
        => (await store.List(directory).ToListAsync()).Select(entry => entry.IsDirectory ? $"{entry.Path}/" : entry.Path).Order().ToArray();

    [Fact]
    public async Task FromMemory_WithoutFiles_StartsEmpty()
        => Assert.Empty(await new MemoryStore().List(string.Empty).ToListAsync());

    [Fact]
    public async Task List_ShowsTheEntriesOfAFolderAndNoneBeyondIt()
    {
        Assert.Equal(["a.txt", "dir/"], await Names(string.Empty));
        Assert.Equal(["b.txt", "deep/"], await Names("dir"));
        Assert.Equal(["c.txt"], await Names("dir/deep"));
    }

    [Fact]
    public async Task List_OfAMissingFolderOrAFile_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.List("nope").ToListAsync().AsTask());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.List("a.txt").ToListAsync().AsTask());
    }

    [Fact]
    public async Task Read_ReturnsTheTextOrSaysTheFileIsMissing()
    {
        Assert.Equal("B", await store.ReadText("dir/b.txt"));
        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadText("nope.txt"));
        Assert.Equal("The file 'nope.txt' does not exist.", exception.Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadText("dir"));
    }

    [Fact]
    public async Task Write_CreatesFoldersAndReplacesContent()
    {
        await store.WriteText("new/er/file.txt", "one");
        await store.WriteText("new/er/file.txt", "two");

        Assert.Equal("two", await store.ReadText("new/er/file.txt"));
        Assert.Equal(["er/"], await Names("new"));
    }

    [Fact]
    public async Task Write_OverAFolderOrThroughAFile_Throws()
    {
        await Assert.ThrowsAsync<IOException>(() => store.WriteText("dir", "x"));
        await Assert.ThrowsAsync<IOException>(() => store.WriteText("", "x"));
        IOException exception = await Assert.ThrowsAsync<IOException>(() => store.WriteText("a.txt/inside.txt", "x"));
        Assert.Contains("is a file", exception.Message);
    }

    [Fact]
    public async Task Delete_RemovesFilesAndFoldersAlsoWhenTheyAreNotEmpty()
    {
        await store.Delete("a.txt");
        await store.Delete("dir/deep");

        Assert.Equal(["b.txt"], await Names("dir"));
        await store.Delete("dir");
        Assert.DoesNotContain("dir/", await Names(string.Empty));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.Delete("gone"));
    }

    [Fact]
    public async Task Delete_OfAFileKeepsItsFolder()
    {
        await store.Delete("dir/b.txt");
        await store.Delete("dir/deep/c.txt");

        Assert.Equal(["deep/"], await Names("dir"));
        Assert.Empty(await Names("dir/deep"));
    }

    [Fact]
    public async Task Delete_And_Move_RefuseTheRoot()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Delete(string.Empty));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Move(string.Empty, "x"));
    }

    [Fact]
    public async Task Move_MovesAFileAndCreatesTheFoldersOfTheDestination()
    {
        await store.Move("a.txt", "x/y/a.txt");

        Assert.Equal("A", await store.ReadText("x/y/a.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadText("a.txt"));
    }

    [Fact]
    public async Task Move_MovesAFolderWithEverythingInIt()
    {
        await store.Move("dir", "moved");

        Assert.Equal("B", await store.ReadText("moved/b.txt"));
        Assert.Equal("C", await store.ReadText("moved/deep/c.txt"));
        Assert.Equal(["a.txt", "moved/"], await Names(string.Empty));
    }

    [Fact]
    public async Task Move_MovesAnEmptyFolder()
    {
        await store.WriteText("empty/inner/f.txt", "x");
        await store.Delete("empty/inner/f.txt");

        await store.Move("empty", "elsewhere");

        Assert.Equal(["inner/"], await Names("elsewhere"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.List("empty").ToListAsync().AsTask());
    }

    [Theory]
    [InlineData("a.txt", "dir/b.txt")]
    [InlineData("a.txt", "dir")]
    [InlineData("dir", "a.txt")]
    [InlineData("a.txt", "")]
    public async Task Move_ToAnExistingDestination_Throws(string from, string to)
    {
        await Assert.ThrowsAsync<IOException>(() => store.Move(from, to));

        Assert.Equal("A", await store.ReadText("a.txt"));
    }

    [Fact]
    public async Task Move_OfAFolderIntoItself_ThrowsAndChangesNothing()
    {
        IOException exception = await Assert.ThrowsAsync<IOException>(() => store.Move("dir", "dir/inside/dir"));

        Assert.Contains("into itself", exception.Message);
        Assert.Equal("C", await store.ReadText("dir/deep/c.txt"));
    }

    [Fact]
    public async Task Move_OfSomethingMissing_Throws()
        => await Assert.ThrowsAsync<FileNotFoundException>(() => store.Move("nope", "x"));

    [Fact]
    public async Task Move_ThroughAFileAsADestinationFolder_ThrowsAndChangesNothing()
    {
        await Assert.ThrowsAsync<IOException>(() => store.Move("dir/b.txt", "a.txt/b.txt"));

        Assert.Equal("B", await store.ReadText("dir/b.txt"));
    }

    [Fact]
    public async Task FromMemory_Instances_AreIndependent()
    {
        IStore first = new MemoryStore();
        IStore second = new MemoryStore();

        await first.WriteText("only-here.txt", "x");

        await Assert.ThrowsAsync<FileNotFoundException>(() => second.ReadText("only-here.txt"));
    }

    [Fact]
    public async Task Calls_AtTheSameTime_NeverLoseAWrite()
    {
        IStore memory = new MemoryStore();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(async () =>
        {
            await memory.WriteText($"folder{index % 10}/file{index}.txt", index.ToString());
            await memory.List(string.Empty).ToListAsync();
        })));

        Assert.Equal(10, (await memory.List(string.Empty).ToListAsync()).Count);
        Assert.Equal(200, (await Task.WhenAll(Enumerable.Range(0, 10).Select(async folder => (await memory.List($"folder{folder}").ToListAsync()).Count))).Sum());
    }

    [Fact]
    public async Task ItWorksAsAMountAndWithCopy()
    {
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["mem"] = store, ["ref"] = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["guide.md"] = "the guide" }) });

        await mounted.Copy("ref/guide.md", "mem/guide-copy.md");
        await mounted.Move("mem/dir", "mem/folder");

        Assert.Equal("the guide", await store.ReadText("guide-copy.md"));
        Assert.Equal("C", await store.ReadText("folder/deep/c.txt"));
    }

    [Fact]
    public async Task Write_And_Read_AreByteForByteForAnyContent()
    {
        byte[] bytes = [0, 255, 10, 13, 0xEF, 0xBB, 0xBF, 128, 0, 1];
        using MemoryStream source = new(bytes);

        await store.WriteFrom("bin/data.dat", source);

        await using Stream read = await store.Open("bin/data.dat", StoreOpenMode.Read);
        using MemoryStream copy = new();
        await read.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Write_KeepsItsOwnCopyAndLeavesTheSourceOpen()
    {
        byte[] bytes = [1, 2, 3];
        using MemoryStream source = new(bytes);

        await store.WriteFrom("copy.dat", source);
        bytes[0] = 99;

        await using Stream read = await store.Open("copy.dat", StoreOpenMode.Read);
        Assert.Equal(1, read.ReadByte());
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Read_GivesEveryCallerItsOwnReadOnlyStream()
    {
        await using Stream first = await store.Open("a.txt", StoreOpenMode.Read);
        await using Stream second = await store.Open("a.txt", StoreOpenMode.Read);

        Assert.False(first.CanWrite);
        Assert.Equal(65, first.ReadByte());
        Assert.Equal(65, second.ReadByte());
    }

    [Fact]
    public async Task WriteText_StoresUtf8WithoutAByteOrderMark()
    {
        IStore memory = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["u.txt"] = "héllo ✓" });

        await using Stream read = await memory.Open("u.txt", StoreOpenMode.Read);
        using MemoryStream copy = new();
        await read.CopyToAsync(copy);

        Assert.Equal(Encoding.UTF8.GetBytes("héllo ✓"), copy.ToArray());
        Assert.NotEqual(0xEF, copy.ToArray()[0]);
    }
}
