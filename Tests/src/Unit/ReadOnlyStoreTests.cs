namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ReadOnlyStoreTests
{
    private readonly FakeStore inner = new(new Dictionary<string, string> { ["a.txt"] = "A", ["dir/b.txt"] = "B" });

    private readonly IStore view;

    public ReadOnlyStoreTests()
        => view = inner.AsReadOnly();

    [Fact]
    public async Task ListAndRead_PassThrough()
    {
        Assert.Equal("A", await view.ReadText("a.txt"));
        Assert.Equal(["a.txt", "dir"], (await view.List(string.Empty).ToListAsync()).Select(entry => entry.Path).Order());
    }

    [Fact]
    public async Task WriteDeleteAndMove_AreRefusedAndChangeNothing()
    {
        Dictionary<string, string> before = new(inner.Files);

        UnauthorizedAccessException write = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.WriteText("a.txt", "changed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.Delete("a.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.Move("a.txt", "c.txt"));

        Assert.Equal("'a.txt' is read-only: it cannot be changed.", write.Message);
        Assert.Equal(before, inner.Files);
    }

    [Theory]
    [InlineData(StoreOpenMode.Create)]
    [InlineData(StoreOpenMode.Open)]
    [InlineData(StoreOpenMode.Replace)]
    [InlineData(StoreOpenMode.Append)]
    public async Task Open_InAnyModeThatCouldChangeIt_IsRefusedAndChangesNothing(StoreOpenMode mode)
    {
        UnauthorizedAccessException exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.Open("a.txt", mode));

        Assert.Equal("'a.txt' is read-only: it cannot be changed.", exception.Message);
        Assert.Equal("A", inner.Files["a.txt"]);
    }

    [Fact]
    public async Task IsFolderAndGetSize_PassThrough()
    {
        Assert.True(await view.IsFolder("dir"));
        Assert.False(await view.IsFolder("a.txt"));
        Assert.Equal(2, await view.GetSize(string.Empty));
        Assert.Equal(1, await view.GetSize("dir"));
    }

    [Fact]
    public async Task CreateFolderCopyAndTransfer_AreRefusedAndChangeNothing()
    {
        Dictionary<string, string> before = new(inner.Files);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.CreateFolder("new"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.Copy("a.txt", "copy.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ((IStoreTransfer)view).TryMoveTo("a.txt", IStore.FromMemory(), "a.txt"));

        Assert.Equal(before, inner.Files);
    }

    [Fact]
    public void Inner_IsTheStoreTheViewWasMadeOf()
        => Assert.Same(inner, Assert.IsType<ReadOnlyStore>(view).Inner);

    [Fact]
    public async Task Read_PassesTheStreamThrough()
    {
        await using Stream read = await view.Open("a.txt", StoreOpenMode.Read);

        Assert.Equal(65, read.ReadByte());
    }

    [Fact]
    public async Task Copy_OutOfAReadOnlyStoreWorksAndIntoItIsRefused()
    {
        FakeStore other = new();
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["ref"] = view, ["main"] = other });

        await mounted.Copy("ref/a.txt", "main/a-copy.txt");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Copy("main/a-copy.txt", "ref/new.txt"));

        Assert.Equal("A", other.Files["a-copy.txt"]);
        Assert.DoesNotContain("new.txt", inner.Files.Keys);
    }
}
