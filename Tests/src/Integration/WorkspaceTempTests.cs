namespace Markwardt.LiveAi.Tests.Integration;

public sealed class WorkspaceTempTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly ManualTime time = new();

    private readonly List<StoragePool> pools = [];

    private readonly List<WorkspaceTemp> made = [];

    private string Workspace => temp.Path;

    private string Folder => System.IO.Path.Combine(Workspace, ".liveai-temp");

    public Task InitializeAsync()
        => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (WorkspaceTemp item in made)
        {
            item.Dispose();
        }

        foreach (StoragePool pool in pools)
        {
            await pool.DisposeAsync();
        }

        temp.Dispose();
    }

    private WorkspaceTemp Make(string owner = "one", TimeSpan? retention = null)
    {
        StoragePool pool = TestStorage.Pool(null, time: time, retention: retention ?? TimeSpan.FromMinutes(10));
        pools.Add(pool);
        WorkspaceTemp item = new(Workspace, ".liveai-temp", owner, pool);
        made.Add(item);
        return item;
    }

    private string Put(string relative, string text = "x")
    {
        string path = System.IO.Path.Combine(Folder, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Touch_OfAPathOutsideTheFolder_MakesNothing()
    {
        WorkspaceTemp item = Make();

        item.Touch("src/a.txt");
        item.Touch(".liveai-temporary/a.txt");

        Assert.False(Directory.Exists(Folder));
    }

    [Theory]
    [InlineData(".liveai-temp/a.txt")]
    [InlineData("./x/../.liveai-temp/a.txt")]
    [InlineData(".liveai-temp")]
    public void Touch_OfAPathInTheFolder_MakesItWithAMarkerAndALock(string path)
    {
        WorkspaceTemp item = Make();

        item.Touch(path);

        Assert.True(item.IsUsable);
        Assert.Equal("*\n", File.ReadAllText(System.IO.Path.Combine(Folder, ".gitignore")));
        Assert.True(File.Exists(System.IO.Path.Combine(Folder, ".live-one")));
    }

    [Fact]
    public void AFolderSomethingElseMade_IsNotUsableAndNeverTouched()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(System.IO.Path.Combine(Folder, "theirs.txt"), "mine");
        WorkspaceTemp item = Make();

        item.Touch(".liveai-temp/a.txt");
        item.Command().Dispose();
        item.Sweep();
        time.Advance(TimeSpan.FromDays(1));
        item.Sweep();
        item.Dispose();

        Assert.False(item.IsUsable);
        Assert.Equal(["theirs.txt"], Directory.GetFileSystemEntries(Folder).Select(System.IO.Path.GetFileName));
    }

    [Fact]
    public void AnEmptyFolderWithoutAMarker_IsTakenOver()
    {
        Directory.CreateDirectory(Folder);
        WorkspaceTemp item = Make();

        Assert.True(item.IsUsable);
        item.Touch(".liveai-temp/a.txt");

        Assert.True(File.Exists(System.IO.Path.Combine(Folder, ".gitignore")));
    }

    [Fact]
    public void WhenItIsFirstUsed_WhatAnEndedSessionLeftIsRemoved()
    {
        Put("old.txt");
        Put("dir/deep.txt");
        File.WriteAllText(System.IO.Path.Combine(Folder, ".gitignore"), "*\n");
        File.WriteAllText(System.IO.Path.Combine(Folder, ".live-dead"), string.Empty);
        WorkspaceTemp item = Make();

        item.Touch(".liveai-temp/new.txt");

        Assert.Equal([".gitignore", ".live-one"], Directory.GetFileSystemEntries(Folder).Select(System.IO.Path.GetFileName).Order());
    }

    [Fact]
    public void WhenItIsFirstUsed_WhatALiveSessionHasIsKept()
    {
        WorkspaceTemp first = Make("first");
        first.Touch(".liveai-temp/a.txt");
        string kept = Put("a.txt");
        WorkspaceTemp second = Make("second");

        second.Touch(".liveai-temp/b.txt");

        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(System.IO.Path.Combine(Folder, ".live-first")));
        Assert.True(File.Exists(System.IO.Path.Combine(Folder, ".live-second")));
    }

    [Fact]
    public void Sweep_RemovesEntriesNotUsedForTheRetentionAndKeepsTheOthers()
    {
        WorkspaceTemp item = Make();
        item.Touch(".liveai-temp/x");
        string idle = Put("idle.txt", "idle");
        string busy = Put("busy.txt", "busy");
        string folder = Put("dir/inner.txt");
        item.Sweep();

        time.Advance(TimeSpan.FromMinutes(6));
        File.WriteAllText(busy, "busy and changed");
        item.Sweep();
        time.Advance(TimeSpan.FromMinutes(6));
        item.Sweep();

        Assert.False(File.Exists(idle));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(folder)));
        Assert.True(File.Exists(busy));
        Assert.True(File.Exists(System.IO.Path.Combine(Folder, ".gitignore")));
    }

    [Fact]
    public void Sweep_CountsATouchAsUse()
    {
        WorkspaceTemp item = Make();
        item.Touch(".liveai-temp/x");
        string kept = Put("kept.txt");
        item.Sweep();

        time.Advance(TimeSpan.FromMinutes(8));
        item.Touch(".liveai-temp/kept.txt");
        time.Advance(TimeSpan.FromMinutes(8));
        item.Sweep();

        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void Sweep_WhileACommandRuns_RemovesNothing_AndAfterItEndsTheEntriesCountAsUsed()
    {
        WorkspaceTemp item = Make();
        item.Touch(".liveai-temp/a.txt");
        string path = Put("a.txt");
        item.Sweep();

        IDisposable command = item.Command();
        time.Advance(TimeSpan.FromHours(2));
        item.Sweep();
        Assert.True(File.Exists(path));

        command.Dispose();
        item.Sweep();
        Assert.True(File.Exists(path));

        time.Advance(TimeSpan.FromMinutes(11));
        item.Sweep();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TheTimerOfThePool_SweepsOnItsOwn()
    {
        WorkspaceTemp item = Make(retention: TimeSpan.FromMinutes(4));
        item.Touch(".liveai-temp/x");
        string path = Put("a.txt");

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(File.Exists(path));
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Dispose_RemovesTheFolderWhenNoOtherSessionUsesIt()
    {
        WorkspaceTemp item = Make();
        item.Touch(".liveai-temp/a.txt");
        Put("a.txt");

        item.Dispose();

        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void Dispose_LeavesTheFolderToAnotherLiveSession_AndRemovesItsOwnLock()
    {
        WorkspaceTemp first = Make("first");
        first.Touch(".liveai-temp/a.txt");
        WorkspaceTemp second = Make("second");
        second.Touch(".liveai-temp/a.txt");
        string path = Put("a.txt");

        first.Dispose();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(System.IO.Path.Combine(Folder, ".live-first")));

        second.Dispose();

        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void ACommand_MakesTheFolder()
    {
        WorkspaceTemp item = Make();

        using IDisposable command = item.Command();

        Assert.True(File.Exists(System.IO.Path.Combine(Folder, ".gitignore")));
    }

    [Theory]
    [InlineData(".liveai-temp/.gitignore", true)]
    [InlineData("./.liveai-temp/.live-abc", true)]
    [InlineData(".liveai-temp/a.txt", false)]
    [InlineData(".liveai-temp/dir/.gitignore", false)]
    [InlineData("src/.gitignore", false)]
    public void IsInternal_IsTrueOnlyForTheMarkerAndTheLocksOfTheFolder(string path, bool expected)
        => Assert.Equal(expected, Make().IsInternal(path));

    [Fact]
    public async Task TheStoreOfTheWorkspace_LeavesTheMarkerAndTheLocksOutOfListings()
    {
        WorkspaceTemp item = Make();
        IStore store = new WorkspaceTempStore(DiskStore.Open(Workspace), item);
        await store.WriteText(".liveai-temp/a.txt", "x");
        await store.WriteText("plain.txt", "y");

        List<string> everything = (await store.List(string.Empty, true).ToListAsync()).Select(entry => entry.Path).Order().ToList();
        List<string> inside = (await store.List(".liveai-temp").ToListAsync()).Select(entry => entry.Path).ToList();

        Assert.Equal([".liveai-temp", ".liveai-temp/a.txt", "plain.txt"], everything);
        Assert.Equal(["a.txt"], inside);
        Assert.True(File.Exists(System.IO.Path.Combine(Folder, ".gitignore")));
    }
}
