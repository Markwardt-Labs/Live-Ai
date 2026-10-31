namespace Markwardt.LiveAi.Tests.Unit;

public sealed class MountedWorkspaceTests
{
    private async Task<IStore> Memory(params (string Path, string Text)[] files)
        => await TestStoreExtensions.Memory(files.ToDictionary(file => file.Path, file => file.Text));

    private async Task<(MountedStore Store, IStore Workspace, IStore Test, IStore Docs)> Setup()
    {
        IStore workspace = await Memory(("readme.md", "workspace readme"), ("src/main.cs", "main"), ("my/other.txt", "other"), ("my/test/shadowed.txt", "hidden by the mount"));
        IStore test = await Memory(("thing/file.txt", "from the mount"), ("root.txt", "root"));
        IStore docs = await Memory(("guide.md", "guide"));
        return (new MountedStore(workspace, new Dictionary<string, IStore> { ["my/test"] = test, ["docs"] = docs }), workspace, test, docs);
    }

    private async Task<string[]> Names(IStore store, string directory, bool recursive = false)
        => (await store.List(directory, recursive).ToListAsync()).Select(entry => entry.IsDirectory ? $"{entry.Path}/" : entry.Path).Order().ToArray();

    [Fact]
    public async Task ARelativePath_IsInTheWorkspaceUnlessItStartsWithTheMountPath()
    {
        (MountedStore store, _, _, _) = await Setup();

        Assert.Equal("workspace readme", await store.ReadText("readme.md"));
        Assert.Equal("main", await store.ReadText("./src/../src/main.cs"));
        Assert.Equal("from the mount", await store.ReadText("my/test/thing/file.txt"));
        Assert.Equal("guide", await store.ReadText("docs/guide.md"));
        Assert.Equal("other", await store.ReadText("my/other.txt"));
    }

    [Fact]
    public async Task AMount_HidesWhatTheWorkspaceHasAtItsPath()
    {
        (MountedStore store, _, _, _) = await Setup();

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadText("my/test/shadowed.txt"));
        Assert.Equal(["root.txt", "thing/"], await Names(store, "my/test"));
    }

    [Fact]
    public async Task TheLongestMountPathWins()
    {
        IStore outer = await Memory(("test/x.txt", "from the outer mount"), ("other.txt", "o"));
        IStore inner = await Memory(("x.txt", "from the inner mount"));
        MountedStore store = new(null, new Dictionary<string, IStore> { ["my"] = outer, ["my/test"] = inner });

        Assert.Equal("from the inner mount", await store.ReadText("my/test/x.txt"));
        Assert.Equal("o", await store.ReadText("my/other.txt"));
    }

    [Fact]
    public async Task List_MergesTheWorkspaceWithTheFoldersOnTheWayToTheMounts()
    {
        (MountedStore store, _, _, _) = await Setup();

        Assert.Equal(["docs/", "my/", "readme.md", "src/"], await Names(store, string.Empty));
        Assert.Equal(["other.txt", "test/"], await Names(store, "my"));
        Assert.Equal(["guide.md"], await Names(store, "docs"));
    }

    [Fact]
    public async Task List_Recursively_ShowsTheMountsContentAtTheirPathsAndNotWhatTheyHide()
    {
        (MountedStore store, _, _, _) = await Setup();

        string[] everything = await Names(store, string.Empty, true);

        Assert.Equal(["docs/", "docs/guide.md", "my/", "my/other.txt", "my/test/", "my/test/root.txt", "my/test/thing/", "my/test/thing/file.txt", "readme.md", "src/", "src/main.cs"], everything);
        Assert.DoesNotContain("my/test/shadowed.txt", everything);
    }

    [Fact]
    public async Task List_OfAFolderTheWorkspaceDoesNotHaveButAMountIsUnder_ShowsTheWayToTheMount()
    {
        IStore workspace = await Memory(("readme.md", "r"));
        MountedStore store = new(workspace, new Dictionary<string, IStore> { ["deep/er/mount"] = await Memory(("a.txt", "a")) });

        Assert.Equal(["er/"], await Names(store, "deep"));
        Assert.Equal(["mount/"], await Names(store, "deep/er"));
        Assert.Equal(["deep/", "deep/er/", "deep/er/mount/", "deep/er/mount/a.txt", "readme.md"], await Names(store, string.Empty, true));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.List("nothing").ToListAsync().AsTask());
    }

    [Fact]
    public async Task WithoutAWorkspace_OnlyTheMountsCanBeReached()
    {
        MountedStore store = new(null, new Dictionary<string, IStore> { ["my/test"] = await Memory(("a.txt", "a")) });

        Assert.Equal(["my/"], await Names(store, string.Empty));
        Assert.Equal(["test/"], await Names(store, "my"));
        Assert.Equal("a", await store.ReadText("my/test/a.txt"));
        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadText("loose.txt"));
        Assert.Equal("'loose.txt' is not inside a mounted folder. The mounted folders are: my/test.", exception.Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadText("/etc/hostname"));
    }

    [Fact]
    public async Task AnAbsolutePathOrOneThatLeadsOutside_IsTheWorkspacesOwnBusiness()
    {
        Mock<IStore> workspace = new();
        workspace.Setup(mock => mock.Open(It.IsAny<string>(), StoreOpenMode.Read, It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("refused"));
        MountedStore store = new(workspace.Object, new Dictionary<string, IStore> { ["docs"] = await Memory(("a.txt", "a")) });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadText("/etc/hostname"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadText("../secret.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadText("docs/../../secret.txt"));

        workspace.Verify(mock => mock.Open("/etc/hostname", StoreOpenMode.Read, It.IsAny<CancellationToken>()), Times.Once);
        workspace.Verify(mock => mock.Open("../secret.txt", StoreOpenMode.Read, It.IsAny<CancellationToken>()), Times.Once);
        workspace.Verify(mock => mock.Open("docs/../../secret.txt", StoreOpenMode.Read, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsFolder_IsTrueForTheWorkspaceItsFoldersTheMountsAndTheWayToThem()
    {
        (MountedStore store, _, _, _) = await Setup();

        Assert.True(await store.IsFolder(string.Empty));
        Assert.True(await store.IsFolder("src"));
        Assert.True(await store.IsFolder("my"));
        Assert.True(await store.IsFolder("my/test"));
        Assert.True(await store.IsFolder("my/test/thing"));
        Assert.True(await store.IsFolder("docs"));
        Assert.False(await store.IsFolder("readme.md"));
        Assert.False(await store.IsFolder("nothing"));
        Assert.False(await store.IsFolder("my/test/nothing"));
    }

    [Fact]
    public async Task GetSize_OfTheRootAddsTheWorkspaceAndTheMountsAndOfAFolderOnTheWayAddsTheMountsBelow()
    {
        (MountedStore store, IStore workspace, IStore test, IStore docs) = await Setup();

        long all = await store.GetSize(string.Empty);

        Assert.Equal(await workspace.GetSize(string.Empty) + await test.GetSize(string.Empty) + await docs.GetSize(string.Empty), all);
        Assert.Equal(await workspace.GetSize("my") + await test.GetSize(string.Empty), await store.GetSize("my"));
        Assert.Equal(await test.GetSize("thing/file.txt"), await store.GetSize("my/test/thing/file.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.GetSize("missing.txt"));
    }

    [Fact]
    public async Task Writes_GoToTheWorkspaceOrTheMountThePathIsIn()
    {
        (MountedStore store, IStore workspace, IStore test, _) = await Setup();

        await store.WriteText("new/in-workspace.txt", "w");
        await store.WriteText("my/test/new/in-mount.txt", "m");
        await store.CreateFolder("made/folder");
        await store.CreateFolder("my/test");

        Assert.Equal("w", await workspace.ReadText("new/in-workspace.txt"));
        Assert.Equal("m", await test.ReadText("new/in-mount.txt"));
        Assert.True(await workspace.IsFolder("made/folder"));
    }

    [Fact]
    public async Task AMountedFolderItselfAndTheFoldersWithOneInThem_CanNotBeDeletedOrMoved()
    {
        (MountedStore store, IStore workspace, _, _) = await Setup();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Delete("my/test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Delete("my"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Delete("docs"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Move("my", "elsewhere"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Move("readme.md", "my/test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.Move("readme.md", "my"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.WriteText("my/test", "file"));
        Assert.True(await workspace.IsFolder("my"));
    }

    [Fact]
    public async Task Delete_OfAFileInsideAMountOrTheWorkspace_Works()
    {
        (MountedStore store, IStore workspace, IStore test, _) = await Setup();

        await store.Delete("my/test/root.txt");
        await store.Delete("readme.md");

        await Assert.ThrowsAsync<FileNotFoundException>(() => test.ReadText("root.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => workspace.ReadText("readme.md"));
    }

    [Fact]
    public async Task Copy_AndMove_WorkWithinOneStoreAndBetweenTheWorkspaceAndAMount()
    {
        (MountedStore store, IStore workspace, IStore test, _) = await Setup();

        await store.Copy("readme.md", "my/test/copied.md");
        await store.Move("src/main.cs", "my/test/moved.cs");
        await store.Copy("my/test/root.txt", "my/test/inside.txt");
        await store.Move("my/test/thing", "my/test/renamed");
        await store.Copy("my/test/root.txt", "from-mount.txt");

        Assert.Equal("workspace readme", await test.ReadText("copied.md"));
        Assert.Equal("main", await test.ReadText("moved.cs"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => workspace.ReadText("src/main.cs"));
        Assert.Equal("root", await test.ReadText("inside.txt"));
        Assert.Equal("from the mount", await test.ReadText("renamed/file.txt"));
        Assert.Equal("root", await workspace.ReadText("from-mount.txt"));
    }
}
