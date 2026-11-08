namespace Markwardt.LiveAi.Tests.Integration;

public sealed class TieredStoreTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly List<StoragePool> pools = [];

    private string Root => System.IO.Path.Combine(temp.Path, "session");

    public Task InitializeAsync()
        => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (StoragePool pool in pools)
        {
            await pool.DisposeAsync();
        }

        temp.Dispose();
    }

    private StoragePool Pool(long memoryLimit = 1000, long fileLimit = 1_000_000, TimeProvider? time = null, TimeSpan? retention = null, string? root = null)
    {
        StoragePool pool = TestStorage.Pool(root ?? Root, memoryLimit, fileLimit, time, retention);
        pools.Add(pool);
        return pool;
    }

    private string[] Spilled(string store)
        => Directory.Exists(System.IO.Path.Combine(Root, store)) ? Directory.GetFiles(System.IO.Path.Combine(Root, store), "*.bin") : [];

    private async Task<bool> Has(IStore store, string path)
    {
        try
        {
            await store.GetSize(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private string Text(int length, char letter)
        => new(letter, length);

    [Fact]
    public async Task Files_StayInMemoryUntilTheyPassTheLimit()
    {
        StoragePool pool = Pool(100);
        IStore store = pool.Create("session", false);

        await store.WriteText("a.txt", Text(60, 'a'));
        await pool.Settle();

        Assert.Equal(60, pool.MemoryUsed);
        Assert.Empty(Spilled("session"));
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task Files_PassingTheLimit_MoveToDiskLeastRecentlyUsedFirstAndStayReadable()
    {
        StoragePool pool = Pool(100);
        IStore store = pool.Create("session", false);

        await store.WriteText("a.txt", Text(60, 'a'));
        await store.WriteText("b.txt", Text(60, 'b'));
        await pool.Settle();

        Assert.Equal(60, pool.MemoryUsed);
        Assert.Single(Spilled("session"));
        Assert.Equal(Text(60, 'a'), await store.ReadText("a.txt"));
        Assert.Equal(Text(60, 'b'), await store.ReadText("b.txt"));
        Assert.Equal(60, await store.GetSize("a.txt"));
    }

    [Fact]
    public async Task Files_OfBothStoresShareOneMemoryLimit()
    {
        StoragePool pool = Pool(100);
        IStore cache = pool.Create("session", false);
        IStore tmp = pool.Create("cache", true);

        await cache.WriteText("a.txt", Text(60, 'a'));
        await tmp.WriteText("b.txt", Text(60, 'b'));
        await pool.Settle();

        Assert.Equal(60, pool.MemoryUsed);
        Assert.Single(Spilled("session"));
        Assert.Empty(Spilled("cache"));
    }

    [Fact]
    public async Task AFileWrittenInOneGo_PastTheLimit_GoesStraightToDisk()
    {
        StoragePool pool = Pool(100);
        IStore store = pool.Create("session", false);

        await store.WriteText("big.txt", Text(500, 'x'));
        await pool.Settle();

        Assert.Equal(0, pool.MemoryUsed);
        Assert.Single(Spilled("session"));
        Assert.Equal(Text(500, 'x'), await store.ReadText("big.txt"));
    }

    [Fact]
    public async Task ZeroMemoryLimit_KeepsEverythingInFiles()
    {
        StoragePool pool = Pool(0);
        IStore store = pool.Create("session", false);

        await store.WriteText("a.txt", "one");
        await store.WriteText("b.txt", "two");
        await pool.Settle();

        Assert.Equal(0, pool.MemoryUsed);
        Assert.Equal(2, Spilled("session").Length);
        Assert.Equal("two", await store.ReadText("b.txt"));
    }

    [Fact]
    public async Task ADeletedFile_RemovesItsDiskFile()
    {
        StoragePool pool = Pool(0);
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", "one");
        await pool.Settle();

        await store.Delete("a.txt");

        Assert.Empty(Spilled("session"));
    }

    [Fact]
    public async Task AFileOnDisk_ReadOftenEnoughAfterAWhile_MovesBackToMemory()
    {
        ManualTime time = new();
        StoragePool pool = Pool(400, time: time);
        IStore store = pool.Create("session", false);

        foreach (string name in new[] { "a", "b", "c", "d", "e" })
        {
            await store.WriteText($"{name}.txt", Text(100, name[0]));
        }

        await pool.Settle();
        Assert.Single(Spilled("session"));

        await store.Delete("b.txt");
        await store.Delete("c.txt");
        await store.Delete("d.txt");
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(Text(100, 'a'), await store.ReadText("a.txt"));
        Assert.Equal(Text(100, 'a'), await store.ReadText("a.txt"));
        await pool.Settle();

        Assert.Empty(Spilled("session"));
        Assert.Equal(200, pool.MemoryUsed);
        Assert.Equal(Text(100, 'a'), await store.ReadText("a.txt"));
    }

    [Fact]
    public async Task AFileOnDisk_ReadOnce_StaysOnDisk()
    {
        ManualTime time = new();
        StoragePool pool = Pool(0, time: time);
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", "one");
        await pool.Settle();
        time.Advance(TimeSpan.FromMinutes(1));

        await store.ReadText("a.txt");
        await pool.Settle();

        Assert.Single(Spilled("session"));
    }

    [Fact]
    public async Task TheTemporaryStore_RemovesFilesThatWereNotUsedForTheRetention()
    {
        ManualTime time = new();
        StoragePool pool = Pool(time: time, retention: TimeSpan.FromMinutes(10));
        IStore store = pool.Create("cache", true);
        await store.WriteText("a.txt", "a");
        await store.WriteText("b.txt", "b");

        time.Advance(TimeSpan.FromMinutes(6));
        await store.ReadText("a.txt");
        time.Advance(TimeSpan.FromMinutes(6));
        await pool.Expire();

        Assert.True(await Has(store, "a.txt"));
        Assert.False(await Has(store, "b.txt"));
    }

    [Fact]
    public async Task TheTemporaryStore_CountsAWriteAsUseButNotAListing()
    {
        ManualTime time = new();
        StoragePool pool = Pool(time: time, retention: TimeSpan.FromMinutes(10));
        IStore store = pool.Create("cache", true);
        await store.WriteText("a.txt", "a");
        await store.WriteText("b.txt", "b");

        time.Advance(TimeSpan.FromMinutes(6));
        await store.WriteText("b.txt", "again");
        time.Advance(TimeSpan.FromMinutes(5));
        _ = await store.List(string.Empty, true).ToListAsync();
        await pool.Expire();

        Assert.False(await Has(store, "a.txt"));
        Assert.Equal("again", await store.ReadText("b.txt"));
    }

    [Fact]
    public async Task TheTemporaryStore_ExpiresFilesOnItsOwnAsTheClockAdvances()
    {
        ManualTime time = new();
        StoragePool pool = Pool(time: time, retention: TimeSpan.FromMinutes(4));
        IStore store = pool.Create("cache", true);
        await store.WriteText("a.txt", "a");

        time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await Has(store, "a.txt"));
        time.Advance(TimeSpan.FromMinutes(3));

        Assert.False(await Has(store, "a.txt"));
    }

    [Fact]
    public async Task TheTemporaryStore_RemovesTheDiskFileOfAnExpiredFile()
    {
        ManualTime time = new();
        StoragePool pool = Pool(0, time: time, retention: TimeSpan.FromMinutes(1));
        IStore store = pool.Create("cache", true);
        await store.WriteText("a.txt", "a");
        await pool.Settle();
        Assert.Single(Spilled("cache"));

        time.Advance(TimeSpan.FromMinutes(2));
        await pool.Expire();

        Assert.Empty(Spilled("cache"));
        Assert.False(await Has(store, "a.txt"));
    }

    [Fact]
    public async Task TheCache_NeverExpires()
    {
        ManualTime time = new();
        StoragePool pool = Pool(time: time, retention: TimeSpan.FromMinutes(1));
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", "a");

        time.Advance(TimeSpan.FromDays(30));
        await pool.Expire();

        Assert.Equal("a", await store.ReadText("a.txt"));
    }

    [Fact]
    public async Task AWriteOverTheFileLimit_Fails_AndStoresNothing()
    {
        StoragePool pool = Pool(fileLimit: 10);
        IStore store = pool.Create("session", false);

        IOException failure = await Assert.ThrowsAsync<IOException>(() => store.WriteText("big.txt", Text(20, 'x')));

        Assert.Contains("Keep large data in the workspace", failure.Message);
        Assert.False(await Has(store, "big.txt"));
        Assert.Empty(await store.List(string.Empty, true).ToListAsync());
        Assert.Equal(0, pool.MemoryUsed);
        Assert.Empty(Spilled("session"));
    }

    [Fact]
    public async Task AWriteOverTheFileLimit_WithNoMemory_LeavesNoDiskFile()
    {
        StoragePool pool = Pool(0, fileLimit: 10);
        IStore store = pool.Create("session", false);

        await Assert.ThrowsAsync<IOException>(() => store.WriteText("big.txt", Text(20, 'x')));

        Assert.False(await Has(store, "big.txt"));
        Assert.Empty(Spilled("session"));
    }

    [Fact]
    public async Task AWriteOfExactlyTheFileLimit_Succeeds()
    {
        StoragePool pool = Pool(fileLimit: 10);
        IStore store = pool.Create("session", false);

        await store.WriteText("a.txt", Text(10, 'x'));

        Assert.Equal(10, await store.GetSize("a.txt"));
    }

    [Fact]
    public async Task AnUnusableDiskFolder_KeepsFilesInMemory_AndSaysSo()
    {
        string blocked = System.IO.Path.Combine(temp.Path, "blocked");
        await File.WriteAllTextAsync(blocked, "a file where the folder should be");
        List<string> reports = [];
        StoragePool pool = new(TimeProvider.System, new PromptStorageSettings { MemoryLimit = 0 }, System.IO.Path.Combine(blocked, "session"), reports.Add);
        pools.Add(pool);
        IStore store = pool.Create("session", false);

        await store.WriteText("a.txt", "kept");
        await pool.Settle();

        Assert.Equal("kept", await store.ReadText("a.txt"));
        Assert.Equal(4, pool.MemoryUsed);
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task NoDiskFolder_KeepsFilesInMemory()
    {
        StoragePool pool = new(TimeProvider.System, new PromptStorageSettings { MemoryLimit = 0 }, null);
        pools.Add(pool);
        IStore store = pool.Create("session", false);

        await store.WriteText("a.txt", "kept");
        await pool.Settle();

        Assert.Equal("kept", await store.ReadText("a.txt"));
        Assert.Equal(4, pool.MemoryUsed);
    }

    [Fact]
    public async Task ChangingTheSettings_AppliesTheNewLimit()
    {
        StoragePool pool = Pool(10_000);
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", Text(100, 'a'));
        await pool.Settle();
        Assert.Empty(Spilled("session"));

        pool.Settings = pool.Settings with { MemoryLimit = 0 };
        await pool.Settle();

        Assert.Single(Spilled("session"));
        Assert.Equal(0, pool.MemoryUsed);
    }

    [Fact]
    public async Task ADisposedPool_RemovesItsFolder()
    {
        StoragePool pool = Pool(0);
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", "one");
        await pool.Settle();
        Assert.True(Directory.Exists(Root));

        await pool.DisposeAsync();

        Assert.False(Directory.Exists(Root));
        Assert.Empty(await store.List(string.Empty, true).ToListAsync());
    }

    [Fact]
    public async Task APoolThatStartsWithStaleFiles_ClearsThemFirst()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(Root, "session"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(Root, "session", "old.bin"), "old");
        StoragePool pool = Pool(0);
        IStore store = pool.Create("session", false);

        await store.WriteText("a.txt", "one");
        await pool.Settle();

        Assert.Single(Spilled("session"));
    }

    [Fact]
    public async Task TheFolderOfALivePool_IsNotClearedByAnotherSession()
    {
        StoragePool pool = Pool(0);
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", "one");
        await pool.Settle();

        bool cleared = StoragePool.TryClear(Root);

        Assert.False(cleared);
        Assert.Single(Spilled("session"));
        Assert.Equal("one", await store.ReadText("a.txt"));
    }

    [Fact]
    public async Task ClearOrphans_RemovesTheStorageOfEndedSessionsOnly()
    {
        string data = System.IO.Path.Combine(temp.Path, "data");
        string dead = System.IO.Path.Combine(data, Guid.NewGuid().ToString("N"));
        string alive = System.IO.Path.Combine(data, Guid.NewGuid().ToString("N"));
        string other = System.IO.Path.Combine(data, "Workspaces");
        Directory.CreateDirectory(System.IO.Path.Combine(dead, "session"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(dead, "session", "x.bin"), "x");
        await File.WriteAllTextAsync(System.IO.Path.Combine(dead, "live"), string.Empty);
        Directory.CreateDirectory(System.IO.Path.Combine(other, "session"));
        StoragePool pool = Pool(0, root: alive);
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", "one");
        await pool.Settle();

        StoragePool.ClearOrphans(data);

        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(System.IO.Path.Combine(other, "session")));
        Assert.True(Directory.Exists(alive));
        Assert.Equal("one", await store.ReadText("a.txt"));
    }

    [Fact]
    public async Task ClearOrphans_LeavesOtherContentOfASessionFolderAlone()
    {
        string data = System.IO.Path.Combine(temp.Path, "data");
        string dead = System.IO.Path.Combine(data, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dead, "session"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(dead, "notes.txt"), "mine");

        StoragePool.ClearOrphans(data);

        Assert.False(Directory.Exists(System.IO.Path.Combine(dead, "session")));
        Assert.True(File.Exists(System.IO.Path.Combine(dead, "notes.txt")));
    }

    [Fact]
    public async Task ADiskFile_CanBeCopiedMovedAndDeletedIndependently()
    {
        StoragePool pool = Pool(0);
        IStore store = pool.Create("session", false);
        await store.WriteText("a.txt", "one");
        await pool.Settle();

        await store.Copy("a.txt", "b.txt");
        await store.Delete("a.txt");

        Assert.Equal("one", await store.ReadText("b.txt"));
        await pool.Settle();
        Assert.Single(Spilled("session"));
    }

    [Fact]
    public async Task AFileMovedBetweenStoresOfAPool_KeepsItsContent_AndTakesTheExpiryOfItsNewStore()
    {
        ManualTime time = new();
        StoragePool pool = Pool(time: time, retention: TimeSpan.FromMinutes(1));
        IStore cache = pool.Create("session", false);
        IStore tmp = pool.Create("cache", true);
        await tmp.WriteText("a.txt", "one");
        await cache.WriteText("b.txt", "two");

        MountedStore mounted = new(null, new Dictionary<string, IStore> { [".liveai-session"] = cache, [".liveai-cache"] = tmp });
        await mounted.Move(".liveai-cache/a.txt", ".liveai-session/a.txt");
        await mounted.Move(".liveai-session/b.txt", ".liveai-cache/b.txt");
        time.Advance(TimeSpan.FromMinutes(2));
        await pool.Expire();

        Assert.Equal("one", await cache.ReadText("a.txt"));
        Assert.False(await Has(tmp, "b.txt"));
    }

    [Fact]
    public async Task ManyWritersAndReaders_NeverSeeTornOrLostFiles()
    {
        StoragePool pool = Pool(1000);
        IStore store = pool.Create("session", false);

        Task[] workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (int round = 0; round < 60; round++)
            {
                string path = $"w{worker}/f{round % 10}.txt";
                string text = Text(100 + round, (char)('a' + worker));
                await store.WriteText(path, text);
                Assert.Equal(text, await store.ReadText(path));

                if (round % 7 == 0)
                {
                    await store.Delete(path);
                    Assert.False(await Has(store, path));
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);
        await pool.Settle();

        Assert.True(pool.MemoryUsed <= 1000);

        foreach (StoreEntry entry in await store.List(string.Empty, true).ToListAsync())
        {
            if (!entry.IsDirectory)
            {
                string text = await store.ReadText(entry.Path);
                Assert.Single(text.Distinct());
            }
        }
    }

    [Fact]
    public void Settings_AreChecked()
    {
        Assert.Throws<ArgumentException>(() => (new PromptStorageSettings { MemoryLimit = -1 }).Check());
        Assert.Throws<ArgumentException>(() => (new PromptStorageSettings { FileLimit = 0 }).Check());
        Assert.Throws<ArgumentException>(() => (new PromptStorageSettings { Retention = TimeSpan.Zero }).Check());
        new PromptStorageSettings { MemoryLimit = 0 }.Check();
    }
}
