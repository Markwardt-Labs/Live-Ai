namespace Markwardt.LiveAi.Tests.Integration;

public sealed class OverlayDiskTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    [Fact]
    public async Task ChangesMadeThroughTheOverlayReachTheFolderOnlyWhenApplied()
    {
        await File.WriteAllTextAsync(temp.Combine("keep.txt"), "keep");
        await File.WriteAllTextAsync(temp.Combine("edit.txt"), "old");
        Directory.CreateDirectory(temp.Combine("gone"));
        await File.WriteAllTextAsync(temp.Combine("gone/file.txt"), "x");
        IOverlayStore overlay = IStore.FromDisk(temp.Path).AsOverlay();

        await overlay.WriteText("edit.txt", "new");
        await overlay.WriteText("sub/added.txt", "added");
        await overlay.Delete("gone");

        Assert.Equal("old", await File.ReadAllTextAsync(temp.Combine("edit.txt")));
        Assert.True(Directory.Exists(temp.Combine("gone")));
        Assert.False(File.Exists(temp.Combine("sub/added.txt")));
        Assert.Equal(4, (await overlay.GetChanges()).Count);

        await overlay.Apply();

        Assert.Equal("new", await File.ReadAllTextAsync(temp.Combine("edit.txt")));
        Assert.Equal("added", await File.ReadAllTextAsync(temp.Combine("sub/added.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(temp.Combine("keep.txt")));
        Assert.False(Directory.Exists(temp.Combine("gone")));
        Assert.Empty(await overlay.GetChanges());
    }

    [Fact]
    public async Task DiscardingLeavesTheFolderAsItWas()
    {
        await File.WriteAllTextAsync(temp.Combine("a.txt"), "a");
        IOverlayStore overlay = IStore.FromDisk(temp.Path).AsOverlay();
        await overlay.Delete("a.txt");
        await overlay.WriteText("b.txt", "b");

        await overlay.Discard();

        Assert.Equal(["a.txt"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
        Assert.Equal("a", await overlay.ReadText("a.txt"));
    }

    [Fact]
    public async Task TheFileToolsWorkOnAnOverlayLikeOnAnyStore()
    {
        await File.WriteAllTextAsync(temp.Combine("note.txt"), "one\ntwo");
        IOverlayStore overlay = IStore.FromDisk(temp.Path).AsOverlay();
        PromptTool edit = new PromptLibraryTools().Create(new MountedStore(null, new Dictionary<string, IStore> { ["work"] = overlay }), ["work"], false).Single(tool => tool.Name == "edit_file");

        await edit.Handler(JsonSerializer.SerializeToElement(new { path = "work/note.txt", old_string = "two", new_string = "three" }), default);

        Assert.Equal("one\nthree", await overlay.ReadText("note.txt"));
        Assert.Equal("one\ntwo", await File.ReadAllTextAsync(temp.Combine("note.txt")));
        Assert.Equal([new StoreChange("note.txt", false, StoreChangeKind.Modified)], await overlay.GetChanges());
    }
}
