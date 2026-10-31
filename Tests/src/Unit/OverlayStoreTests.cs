namespace Markwardt.LiveAi.Tests.Unit;

public sealed class OverlayStoreTests
{
    private async Task<(IStore Lower, IOverlayStore Overlay)> Setup()
    {
        IStore lower = await TestStoreExtensions.Memory(new Dictionary<string, string>
        {
            ["readme.md"] = "hello",
            ["src/a.cs"] = "a",
            ["src/deep/b.cs"] = "b",
            ["docs/x.md"] = "x"
        });
        await lower.CreateFolder("empty");
        return (lower, lower.AsOverlay());
    }

    private async Task<List<string>> Listing(IStore store, string folder = "", bool recursive = true)
        => [.. (await store.List(folder, recursive).ToListAsync()).Select(entry => entry.IsDirectory ? $"{entry.Path}/" : entry.Path).Order()];

    [Fact]
    public async Task AnEmptyOverlay_ShowsTheStoreUnderIt()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();

        Assert.Equal(await Listing(lower), await Listing(overlay));
        Assert.Equal("hello", await overlay.ReadText("readme.md"));
        Assert.Empty(await overlay.GetChanges());
        Assert.True(await overlay.IsFolder("src"));
        Assert.False(await overlay.IsFolder("readme.md"));
        Assert.Equal(1, await overlay.GetSize("src/a.cs"));
    }

    [Fact]
    public async Task Writing_ChangesTheOverlayAndLeavesTheStoreUnderItAlone()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();

        await overlay.WriteText("readme.md", "changed");
        await overlay.WriteText("new/file.txt", "new");

        Assert.Equal("changed", await overlay.ReadText("readme.md"));
        Assert.Equal("new", await overlay.ReadText("new/file.txt"));
        Assert.Equal("hello", await lower.ReadText("readme.md"));
        Assert.False(await lower.IsFolder("new"));
        Assert.Contains("new/file.txt", await Listing(overlay));
        Assert.Equal(1, (await Listing(overlay)).Count(entry => entry == "readme.md"));
    }

    [Fact]
    public async Task OpeningInOpenOrAppendMode_CopiesTheFileUpFirst()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();

        await using (Stream append = await overlay.Open("readme.md", StoreOpenMode.Append))
        {
            await append.WriteAsync("!"u8.ToArray());
        }

        await using (Stream edit = await overlay.Open("src/a.cs", StoreOpenMode.Open))
        {
            edit.Seek(0, SeekOrigin.End);
            await edit.WriteAsync("+"u8.ToArray());
        }

        Assert.Equal("hello!", await overlay.ReadText("readme.md"));
        Assert.Equal("a+", await overlay.ReadText("src/a.cs"));
        Assert.Equal("hello", await lower.ReadText("readme.md"));
        Assert.Equal("a", await lower.ReadText("src/a.cs"));
    }

    [Fact]
    public async Task AppendingToAFileThatIsNowhere_MakesIt()
    {
        (_, IOverlayStore overlay) = await Setup();

        await using (Stream append = await overlay.Open("made/log.txt", StoreOpenMode.Append))
        {
            await append.WriteAsync("one"u8.ToArray());
        }

        Assert.Equal("one", await overlay.ReadText("made/log.txt"));
    }

    [Fact]
    public async Task Create_RefusesAPathThatIsTakenInTheLowerStoreOrTheOverlay()
    {
        (_, IOverlayStore overlay) = await Setup();
        await overlay.WriteText("mine.txt", "m");

        await Assert.ThrowsAsync<IOException>(() => overlay.Open("readme.md", StoreOpenMode.Create));
        await Assert.ThrowsAsync<IOException>(() => overlay.Open("mine.txt", StoreOpenMode.Create));
        await Assert.ThrowsAsync<IOException>(() => overlay.Open("src", StoreOpenMode.Replace));
    }

    [Fact]
    public async Task Open_OfAFileThatIsNotThere_FailsForReadingAndEditing()
    {
        (_, IOverlayStore overlay) = await Setup();

        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Open("nothing", StoreOpenMode.Read));
        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Open("nothing", StoreOpenMode.Open));
    }

    [Fact]
    public async Task Deleting_HidesWhatIsUnderneathWithoutTouchingIt()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();

        await overlay.Delete("readme.md");
        await overlay.Delete("src");

        Assert.DoesNotContain("readme.md", await Listing(overlay));
        Assert.DoesNotContain(await Listing(overlay), entry => entry.StartsWith("src"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Open("src/a.cs", StoreOpenMode.Read));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => overlay.List("src").ToListAsync().AsTask());
        Assert.Equal("hello", await lower.ReadText("readme.md"));
        Assert.True(await lower.IsFolder("src/deep"));
    }

    [Fact]
    public async Task Deleting_AFileThatIsNotThere_OrTheRoot_Fails()
    {
        (_, IOverlayStore overlay) = await Setup();

        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Delete("nothing"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => overlay.Delete(""));
        await overlay.Delete("readme.md");
        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Delete("readme.md"));
    }

    [Fact]
    public async Task ARecreatedFolder_IsEmptyAndDoesNotShowWhatWasDeleted()
    {
        (_, IOverlayStore overlay) = await Setup();

        await overlay.Delete("src");
        await overlay.CreateFolder("src");
        await overlay.WriteText("src/fresh.cs", "f");

        Assert.Equal(["fresh.cs"], await Listing(overlay, "src"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Open("src/a.cs", StoreOpenMode.Read));
        Assert.DoesNotContain("src/deep/", await Listing(overlay));
    }

    [Fact]
    public async Task AFileWrittenOverADeletedOne_ReplacesIt()
    {
        (_, IOverlayStore overlay) = await Setup();

        await overlay.Delete("readme.md");
        await overlay.WriteText("readme.md", "again");

        Assert.Equal("again", await overlay.ReadText("readme.md"));
        Assert.Equal(1, (await Listing(overlay)).Count(entry => entry == "readme.md"));
    }

    [Fact]
    public async Task Deleting_AFileThatTheOverlayChanged_AlsoHidesTheOriginal()
    {
        (_, IOverlayStore overlay) = await Setup();
        await overlay.WriteText("readme.md", "changed");

        await overlay.Delete("readme.md");

        Assert.DoesNotContain("readme.md", await Listing(overlay));
        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Open("readme.md", StoreOpenMode.Read));
    }

    [Fact]
    public async Task Move_RenamesAFileAndAFolderWithEverythingInIt()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();

        await overlay.Move("src", "lib");
        await overlay.Move("readme.md", "lib/README.md");

        Assert.Equal("b", await overlay.ReadText("lib/deep/b.cs"));
        Assert.Equal("hello", await overlay.ReadText("lib/README.md"));
        Assert.DoesNotContain(await Listing(overlay), entry => entry.StartsWith("src") || entry == "readme.md");
        Assert.Equal("b", await lower.ReadText("src/deep/b.cs"));
    }

    [Fact]
    public async Task Move_RefusesWhatWouldReplaceOrLoopOrBeTheRoot()
    {
        (_, IOverlayStore overlay) = await Setup();

        await Assert.ThrowsAsync<IOException>(() => overlay.Move("readme.md", "docs/x.md"));
        await Assert.ThrowsAsync<IOException>(() => overlay.Move("src", "src/inside"));
        await Assert.ThrowsAsync<IOException>(() => overlay.Move("readme.md", "readme.md"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => overlay.Move("", "x"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => overlay.Move("nothing", "elsewhere"));
    }

    [Fact]
    public async Task Copy_CopiesIntoTheOverlay()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();

        await overlay.Copy("docs", "docs2");

        Assert.Equal("x", await overlay.ReadText("docs2/x.md"));
        Assert.False(await lower.IsFolder("docs2"));
    }

    [Fact]
    public async Task Writing_BelowAFile_Fails()
    {
        (_, IOverlayStore overlay) = await Setup();

        await Assert.ThrowsAsync<IOException>(() => overlay.WriteText("readme.md/inner", "x"));
        await Assert.ThrowsAsync<IOException>(() => overlay.CreateFolder("readme.md/inner"));
        await Assert.ThrowsAsync<IOException>(() => overlay.CreateFolder("readme.md"));
        await overlay.CreateFolder("docs");
        await overlay.CreateFolder("brand/new");
        Assert.True(await overlay.IsFolder("brand/new"));
    }

    [Fact]
    public async Task List_MergesTheLayersAndShowsEachEntryOnce()
    {
        (_, IOverlayStore overlay) = await Setup();
        await overlay.WriteText("src/a.cs", "changed");
        await overlay.WriteText("src/c.cs", "c");
        await overlay.Delete("src/deep");

        Assert.Equal(["a.cs", "c.cs"], await Listing(overlay, "src"));
        Assert.Equal(["a.cs", "c.cs"], await Listing(overlay, "src", false));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => overlay.List("nothing").ToListAsync().AsTask());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => overlay.List("readme.md").ToListAsync().AsTask());
    }

    [Fact]
    public async Task Paths_AreCleanedLikeEverywhereElseAndOnesThatLeadOutsideAreRefused()
    {
        (_, IOverlayStore overlay) = await Setup();

        Assert.Equal("hello", await overlay.ReadText("src/../readme.md"));
        await Assert.ThrowsAsync<ArgumentException>(() => overlay.Open("../x", StoreOpenMode.Read));
    }

    [Fact]
    public async Task GetChanges_ListsAddedModifiedAndDeletedAndIgnoresAFileChangedBack()
    {
        (_, IOverlayStore overlay) = await Setup();
        await overlay.WriteText("readme.md", "changed");
        await overlay.WriteText("docs/x.md", "x");
        await overlay.WriteText("added/one.txt", "1");
        await overlay.CreateFolder("added/empty");
        await overlay.Delete("src");
        await overlay.Delete("empty");

        IReadOnlyList<StoreChange> changes = await overlay.GetChanges();

        Assert.Equal(
            [
                new StoreChange("added", true, StoreChangeKind.Added),
                new StoreChange("added/empty", true, StoreChangeKind.Added),
                new StoreChange("added/one.txt", false, StoreChangeKind.Added),
                new StoreChange("empty", true, StoreChangeKind.Deleted),
                new StoreChange("readme.md", false, StoreChangeKind.Modified),
                new StoreChange("src", true, StoreChangeKind.Deleted)
            ],
            changes);
    }

    [Fact]
    public async Task GetChanges_OfADeletedAndRecreatedFolderAndOfAReplacedFile()
    {
        (_, IOverlayStore overlay) = await Setup();
        await overlay.Delete("docs");
        await overlay.CreateFolder("docs");
        await overlay.Delete("readme.md");
        await overlay.WriteText("readme.md", "other");

        IReadOnlyList<StoreChange> changes = await overlay.GetChanges();

        Assert.Equal(
            [
                new StoreChange("docs", true, StoreChangeKind.Added),
                new StoreChange("docs", true, StoreChangeKind.Deleted),
                new StoreChange("readme.md", false, StoreChangeKind.Modified)
            ],
            changes);
    }

    [Fact]
    public async Task Apply_MakesTheStoreUnderItWhatTheOverlayShowsAndEmptiesTheOverlay()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();
        await overlay.WriteText("readme.md", "changed");
        await overlay.WriteText("added/one.txt", "1");
        await overlay.CreateFolder("added/empty");
        await overlay.Delete("src");
        await overlay.Delete("docs");
        await overlay.CreateFolder("docs");
        await overlay.WriteText("docs/new.md", "n");
        List<string> shown = await Listing(overlay);

        await overlay.Apply();

        Assert.Equal(shown, await Listing(lower));
        Assert.Equal("changed", await lower.ReadText("readme.md"));
        Assert.Equal("1", await lower.ReadText("added/one.txt"));
        Assert.True(await lower.IsFolder("added/empty"));
        Assert.False(await lower.IsFolder("src"));
        Assert.DoesNotContain("x.md", await Listing(lower, "docs"));
        Assert.Empty(await overlay.GetChanges());
        Assert.Equal(shown, await Listing(overlay));
    }

    [Fact]
    public async Task Apply_ReplacesAFolderWithAFileAndAFileWithAFolder()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();
        await overlay.Delete("docs");
        await overlay.WriteText("docs", "now a file");
        await overlay.Delete("readme.md");
        await overlay.WriteText("readme.md/inside.txt", "now a folder");

        await overlay.Apply();

        Assert.Equal("now a file", await lower.ReadText("docs"));
        Assert.Equal("now a folder", await lower.ReadText("readme.md/inside.txt"));
    }

    [Fact]
    public async Task Discard_ThrowsEverythingAwayAndAfterwardsWorksAsNew()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();
        await overlay.WriteText("readme.md", "changed");
        await overlay.Delete("src");

        await overlay.Discard();

        Assert.Empty(await overlay.GetChanges());
        Assert.Equal("hello", await overlay.ReadText("readme.md"));
        Assert.True(await overlay.IsFolder("src/deep"));
        Assert.Equal(await Listing(lower), await Listing(overlay));
        await overlay.WriteText("again.txt", "x");
        Assert.Single(await overlay.GetChanges());
    }

    [Fact]
    public async Task AFileOpenedForWriting_IsOnlyTheOverlaysUntilItIsDisposedLikeAnyStore()
    {
        (IStore lower, IOverlayStore overlay) = await Setup();

        await using (Stream stream = await overlay.Open("fresh.txt", StoreOpenMode.Create))
        {
            await stream.WriteAsync("data"u8.ToArray());
        }

        Assert.Equal("data", await overlay.ReadText("fresh.txt"));
        Assert.False(await lower.IsFolder("fresh.txt"));
        Assert.Equal([new StoreChange("fresh.txt", false, StoreChangeKind.Added)], await overlay.GetChanges());
    }

    [Fact]
    public async Task AnOverlayOnAnOverlay_Works()
    {
        (_, IOverlayStore first) = await Setup();
        IOverlayStore second = first.AsOverlay();

        await second.WriteText("readme.md", "second");
        await second.Apply();

        Assert.Equal("second", await first.ReadText("readme.md"));
        Assert.Single(await first.GetChanges());
    }

    [Fact]
    public async Task Open_WithAnUndefinedMode_Throws()
    {
        (_, IOverlayStore overlay) = await Setup();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => overlay.Open("x", (StoreOpenMode)99));
    }
}
