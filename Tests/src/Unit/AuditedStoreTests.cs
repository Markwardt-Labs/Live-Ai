namespace Markwardt.LiveAi.Tests.Unit;

public sealed class AuditedStoreTests
{
    private readonly List<StoreAuditEntry> entries = [];

    private async Task<IStore> Audited()
    {
        IStore inner = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["a.txt"] = "a", ["dir/b.txt"] = "b" });
        return inner.Audited(entries.Add);
    }

    [Fact]
    public async Task EveryAccessIsReportedWithItsOperationPathAndMode()
    {
        IStore store = await Audited();

        await store.List("", true).ToListAsync();
        await store.ReadText("a.txt");
        await store.WriteText("c.txt", "c");
        await store.CreateFolder("made");
        await store.Copy("a.txt", "copy.txt");
        await store.Move("copy.txt", "moved.txt");
        await store.Delete("moved.txt");

        Assert.Equal(
            [
                (StoreOperation.List, "", (StoreOpenMode?)null, (string?)null),
                (StoreOperation.Read, "a.txt", StoreOpenMode.Read, null),
                (StoreOperation.Write, "c.txt", StoreOpenMode.Replace, null),
                (StoreOperation.CreateFolder, "made", null, null),
                (StoreOperation.Copy, "a.txt", null, "copy.txt"),
                (StoreOperation.Move, "copy.txt", null, "moved.txt"),
                (StoreOperation.Delete, "moved.txt", null, null)
            ],
            entries.Select(entry => (entry.Operation, entry.Path, entry.Mode, entry.Destination)));
        Assert.All(entries, entry => Assert.Null(entry.Error));
        Assert.All(entries, entry => Assert.True(DateTimeOffset.UtcNow - entry.Time < TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(StoreOpenMode.Create, "new.txt")]
    [InlineData(StoreOpenMode.Replace, "a.txt")]
    [InlineData(StoreOpenMode.Open, "a.txt")]
    [InlineData(StoreOpenMode.Append, "a.txt")]
    public async Task OpeningInAnyModeThatCanChangeAFile_IsAWrite(StoreOpenMode mode, string path)
    {
        IStore store = await Audited();

        await (await store.Open(path, mode)).DisposeAsync();

        Assert.Equal((StoreOperation.Write, mode), (entries.Single().Operation, entries.Single().Mode!.Value));
    }

    [Fact]
    public async Task AFailedAccessIsReportedWithItsErrorAndStillThrows()
    {
        IStore store = await Audited();

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadText("missing.txt"));
        await Assert.ThrowsAsync<IOException>(() => store.Open("a.txt", StoreOpenMode.Create));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.List("nothing").ToListAsync().AsTask());

        Assert.Equal([StoreOperation.Read, StoreOperation.Write, StoreOperation.List], entries.Select(entry => entry.Operation));
        Assert.Contains("missing.txt", entries[0].Error);
        Assert.NotNull(entries[1].Error);
        Assert.Contains("nothing", entries[2].Error);
    }

    [Fact]
    public async Task AListingIsReportedOnceItEndsAlsoWhenTheCallerStopsEarly()
    {
        IStore store = await Audited();

        await foreach (StoreEntry _ in store.List("", true))
        {
            break;
        }

        Assert.Equal([StoreOperation.List], entries.Select(entry => entry.Operation));
        Assert.Null(entries[0].Error);
    }

    [Fact]
    public async Task QueriesAreNotReportedButStillWork()
    {
        IStore store = await Audited();

        Assert.True(await store.IsFolder("dir"));
        Assert.Equal(1, await store.GetSize("a.txt"));
        Assert.Empty(entries);
    }

    [Fact]
    public async Task TheAsyncFormGetsTheEntryAndTheCancellationToken()
    {
        IStore inner = await TestStoreExtensions.Memory(new Dictionary<string, string> { ["a.txt"] = "a" });
        List<(StoreAuditEntry Entry, CancellationToken Token)> seen = [];
        IStore store = inner.Audited(async (entry, token) =>
        {
            await Task.Yield();
            seen.Add((entry, token));
        });
        using CancellationTokenSource cancellation = new();

        await store.Open("a.txt", StoreOpenMode.Read, cancellation.Token);

        Assert.Equal(cancellation.Token, seen.Single().Token);
        Assert.Equal("a.txt", seen.Single().Entry.Path);
    }

    [Fact]
    public async Task AnExceptionOfTheFunction_ReachesTheCallerAfterTheAccessWasMade()
    {
        IStore inner = IStore.FromMemory();
        IStore store = inner.Audited(_ => throw new InvalidOperationException("audit log is full"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateFolder("made"));

        Assert.Equal("audit log is full", exception.Message);
        Assert.True(await inner.IsFolder("made"));
    }

    [Fact]
    public async Task AMoveBetweenStoresThatMoveNatively_IsReportedOnceAndFallsBackOtherwise()
    {
        IStore first = IStore.FromMemory();
        IStore second = IStore.FromMemory();
        await first.WriteText("a.txt", "a");
        IStore audited = first.Audited(entries.Add);
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["one"] = audited, ["two"] = second });

        await mounted.Move("one/a.txt", "two/a.txt");

        Assert.Equal("a", await second.ReadText("a.txt"));
        Assert.False((await first.List("").ToListAsync()).Any());
        Assert.Contains(entries, entry => entry.Operation == StoreOperation.Delete || entry.Operation == StoreOperation.Move);
    }
}
