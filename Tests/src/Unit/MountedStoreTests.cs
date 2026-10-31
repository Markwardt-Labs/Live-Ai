namespace Markwardt.LiveAi.Tests.Unit;

public sealed class MountedStoreTests
{
    private readonly FakeStore project = new(new Dictionary<string, string> { ["main.txt"] = "main", ["src/a.cs"] = "a" });

    private readonly FakeStore docs = new(new Dictionary<string, string> { ["guide.md"] = "guide", ["api/ref.md"] = "ref" });

    private readonly FakeStore data = new(new Dictionary<string, string> { ["table.csv"] = "1,2" });

    private readonly MountedStore mounted;

    public MountedStoreTests()
        => mounted = new MountedStore(null, new Dictionary<string, IStore> { ["project"] = project, ["docs"] = docs, ["data"] = data });

    private async Task<string[]> Names(string directory)
        => (await mounted.List(directory).ToListAsync()).Select(entry => entry.IsDirectory ? $"{entry.Path}/" : entry.Path).Order().ToArray();

    [Fact]
    public async Task List_AtTheTop_ShowsOnlyTheMountsAsFolders()
        => Assert.Equal(["data/", "docs/", "project/"], await Names(string.Empty));

    [Fact]
    public async Task List_InsideAMount_GoesToThatStore()
    {
        Assert.Equal(["api/", "guide.md"], await Names("docs"));
        Assert.Equal(["ref.md"], await Names("docs/api"));
        Assert.Equal(["main.txt", "src/"], await Names("project"));
    }

    [Fact]
    public async Task List_OfAnythingThatIsNotAMount_SaysWhichAre()
    {
        DirectoryNotFoundException exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => mounted.List("src").ToListAsync().AsTask());

        Assert.Equal("'src' is not inside a mounted folder. The mounted folders are: project, docs, data.", exception.Message);
    }

    [Fact]
    public async Task WithoutMounts_TheTopLevelIsEmptyAndEverythingElseIsRefusedWithThatReason()
    {
        MountedStore none = new(null, new Dictionary<string, IStore>());

        Assert.Empty(await none.List(string.Empty).ToListAsync());
        Assert.Equal("No folders are mounted.", (await Assert.ThrowsAsync<FileNotFoundException>(() => none.ReadText("a.txt"))).Message);
    }

    [Fact]
    public async Task Read_RoutesByTheFirstPartOfThePath()
    {
        Assert.Equal("guide", await mounted.ReadText("docs/guide.md"));
        Assert.Equal("ref", await mounted.ReadText("docs/api/ref.md"));
        Assert.Equal("1,2", await mounted.ReadText("data/table.csv"));
        Assert.Equal("a", await mounted.ReadText("project/src/a.cs"));
    }

    [Fact]
    public async Task Read_OutsideTheMounts_IsNotFound()
        => await Assert.ThrowsAsync<FileNotFoundException>(() => mounted.ReadText("main.txt"));

    [Fact]
    public async Task Write_GoesToTheStoreOfThePathAndIsRefusedAtTheTop()
    {
        await mounted.WriteText("docs/new.md", "new");

        Assert.Equal("new", docs.Files["new.md"]);
        UnauthorizedAccessException exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.WriteText("loose.txt", "x"));
        Assert.Contains("is not inside a mounted folder", exception.Message);
    }

    [Fact]
    public async Task Delete_GoesToTheStoreOfThePathButNeverDeletesAMountItself()
    {
        await mounted.Delete("docs/guide.md");

        Assert.DoesNotContain("guide.md", docs.Files.Keys);
        UnauthorizedAccessException exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Delete("docs"));
        Assert.Contains("mounted folder", exception.Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Delete("loose.txt"));
    }

    [Fact]
    public async Task Move_WithinOneStorage_IsThatStoresMove()
    {
        await mounted.Move("docs/guide.md", "docs/manual.md");

        Assert.Equal("guide", docs.Files["manual.md"]);
        Assert.DoesNotContain("guide.md", docs.Files.Keys);
    }

    [Fact]
    public async Task Move_AcrossStores_CopiesAFileThenDeletesTheOriginal()
    {
        await mounted.Move("project/main.txt", "data/main.txt");

        Assert.Equal("main", data.Files["main.txt"]);
        Assert.DoesNotContain("main.txt", project.Files.Keys);
    }

    [Fact]
    public async Task Move_OfAFolderAcrossStores_MovesEverythingInIt()
    {
        await mounted.Move("project/src", "data/src");

        Assert.Equal("a", data.Files["src/a.cs"]);
        Assert.DoesNotContain(project.Files.Keys, key => key.StartsWith("src/"));
    }

    [Fact]
    public async Task Move_ToAnExistingDestinationAcrossStores_ThrowsAndKeepsTheOriginal()
    {
        data.Files["main.txt"] = "already";

        await Assert.ThrowsAsync<IOException>(() => mounted.Move("project/main.txt", "data/main.txt"));

        Assert.Equal("main", project.Files["main.txt"]);
        Assert.Equal("already", data.Files["main.txt"]);
    }

    [Theory]
    [InlineData("docs", "data/x")]
    [InlineData("data/x", "docs")]
    [InlineData("docs", "data")]
    [InlineData("project/main.txt", "loose.txt")]
    [InlineData("loose.txt", "project/x.txt")]
    public async Task Move_OfAMountItselfOntoOneOrOutsideTheMounts_IsRefused(string from, string to)
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Move(from, to));

    [Fact]
    public async Task Copy_WorksAcrossStoresThroughTheExtension()
    {
        await mounted.Copy("docs", "data/docs-copy");

        Assert.Equal("guide", data.Files["docs-copy/guide.md"]);
        Assert.Equal("ref", data.Files["docs-copy/api/ref.md"]);
        Assert.Equal("guide", docs.Files["guide.md"]);
    }

    [Fact]
    public async Task Copy_ToTheTopLevel_IsRefusedAndOntoAMountIsExisting()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Copy("docs/guide.md", "loose.md"));
        await Assert.ThrowsAsync<IOException>(() => mounted.Copy("docs/guide.md", "data"));
    }

    public static TheoryData<string> MountSpellings => ["docs", "docs/", "docs//", "docs/.", "docs/./", "./docs", "/docs", "docs/x/..", "docs/api/../", "data/../docs"];

    [Theory]
    [MemberData(nameof(MountSpellings))]
    public async Task Delete_OfAMountUnderAnySpelling_IsRefusedAndTheMountSurvives(string path)
    {
        Dictionary<string, string> before = new(docs.Files);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Delete(path));

        Assert.Equal(before, docs.Files);
        Assert.Contains("docs/", await Names(string.Empty));
    }

    [Theory]
    [MemberData(nameof(MountSpellings))]
    public async Task Move_OfAMountUnderAnySpelling_IsRefused(string path)
    {
        Dictionary<string, string> before = new(docs.Files);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Move(path, "data/renamed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Move(path, "renamed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Move("project/main.txt", path));

        Assert.Equal(before, docs.Files);
        Assert.DoesNotContain("renamed", data.Files.Keys);
    }

    [Theory]
    [MemberData(nameof(MountSpellings))]
    public async Task Write_OntoAMountUnderAnySpelling_IsRefusedAndLeavesNothingBehind(string path)
    {
        Dictionary<string, string> before = new(docs.Files);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.WriteText(path, "x"));

        Assert.Equal(before, docs.Files);
    }

    [Theory]
    [InlineData("docs/.")]
    [InlineData("docs//")]
    [InlineData("./docs")]
    [InlineData("docs/api/..")]
    public async Task Copy_OntoAMountUnderAnySpelling_IsRefusedAsExisting(string path)
        => await Assert.ThrowsAsync<IOException>(() => mounted.Copy("project/main.txt", path));

    [Theory]
    [InlineData("docs/./guide.md")]
    [InlineData("docs//guide.md")]
    [InlineData("./docs/api/../guide.md")]
    public async Task Paths_UnderAnySpelling_ReachTheSameFile(string path)
        => Assert.Equal("guide", await mounted.ReadText(path));

    [Theory]
    [InlineData("..")]
    [InlineData("docs/../..")]
    [InlineData("../docs/guide.md")]
    public async Task Paths_ThatClimbAboveTheTop_AreOutside(string path)
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => mounted.ReadText(path));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Delete(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("docs/..")]
    public async Task List_OfTheTopUnderAnySpelling_ShowsTheMounts(string path)
        => Assert.Equal(["data/", "docs/", "project/"], await Names(path));

    [Theory]
    [InlineData(StoreOpenMode.Read)]
    [InlineData(StoreOpenMode.Open)]
    public async Task Open_ForExistingFiles_OutsideTheMounts_IsNotFound(StoreOpenMode mode)
        => await Assert.ThrowsAsync<FileNotFoundException>(() => mounted.Open("loose.txt", mode));

    [Theory]
    [InlineData(StoreOpenMode.Create)]
    [InlineData(StoreOpenMode.Replace)]
    [InlineData(StoreOpenMode.Append)]
    public async Task Open_ForWriting_OutsideTheMountsOrOnAMountItself_IsRefused(StoreOpenMode mode)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Open("loose.txt", mode));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Open("docs", mode));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mounted.Open("docs/.", mode));
        Assert.Equal(["data/", "docs/", "project/"], await Names(string.Empty));
    }

    [Fact]
    public async Task Open_RoutesEveryModeToTheMountedStore()
    {
        await using (Stream stream = await mounted.Open("docs/guide.md", StoreOpenMode.Append))
        {
            await stream.WriteAsync(" more"u8.ToArray());
        }

        await using (Stream stream = await mounted.Open("docs/new.md", StoreOpenMode.Create))
        {
            await stream.WriteAsync("new"u8.ToArray());
        }

        Assert.Equal("guide more", docs.Files["guide.md"]);
        Assert.Equal("new", docs.Files["new.md"]);
        await Assert.ThrowsAsync<IOException>(() => mounted.Open("docs/new.md", StoreOpenMode.Create));
    }
}
