namespace Markwardt.LiveAi.Tests.Integration;

public sealed class ZipStoreTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private string Archive => temp.Combine("archive.zip");

    private void Write(string path, params (string Name, string? Text)[] entries)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach ((string name, string? text) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);

            if (text is not null)
            {
                using StreamWriter writer = new(entry.Open());
                writer.Write(text);
            }
        }
    }

    private List<string> Names(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToList();
    }

    private string Text(string path, string name)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        using StreamReader reader = new(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task FromZip_OfAFileThatDoesNotExist_IsEmptyAndRevealsItsPath()
    {
        IZipStore zip = await IStore.FromZip(Archive);

        Assert.Empty(await zip.List(string.Empty).ToListAsync());
        Assert.Equal(Archive, zip.Path);
        Assert.False(zip.HasChanges);
        Assert.False(File.Exists(Archive));
    }

    [Fact]
    public async Task Save_WritesTheFilesAndTheEmptyFoldersAndClearsTheChanges()
    {
        IZipStore zip = await IStore.FromZip(Archive);
        await zip.WriteText("readme.md", "hello");
        await zip.WriteText("src/deep/code.cs", "class A;");
        await zip.CreateFolder("empty/inner");
        await zip.CreateFolder("src/also");
        Assert.True(zip.HasChanges);

        await zip.Save();

        Assert.False(zip.HasChanges);
        Assert.Equal(["empty/inner/", "readme.md", "src/also/", "src/deep/code.cs"], Names(Archive));
        Assert.Equal("hello", Text(Archive, "readme.md"));
        Assert.Equal("class A;", Text(Archive, "src/deep/code.cs"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task FromZip_ReadsAnExistingArchiveAndSaveReplacesItInPlace()
    {
        Write(Archive, ("a.txt", "A"), ("dir/b.txt", "B"), ("empty/", null));

        IZipStore zip = await IStore.FromZip(Archive);

        Assert.Equal("A", await zip.ReadText("a.txt"));
        Assert.Equal("B", await zip.ReadText("dir/b.txt"));
        Assert.True(await zip.IsFolder("empty"));
        Assert.Equal(2, await zip.GetSize(string.Empty));
        await zip.Delete("a.txt");
        await zip.WriteText("dir/c.txt", "C");
        await zip.Save();
        Assert.Equal(["dir/b.txt", "dir/c.txt", "empty/"], Names(Archive));
        IZipStore again = await IStore.FromZip(Archive);
        Assert.Equal("C", await again.ReadText("dir/c.txt"));
    }

    [Fact]
    public async Task FromZip_AcceptsBackslashesAndRepeatedEntriesAndImpliedFolders()
    {
        Write(Archive, ("win\\file.txt", "first"), ("win/file.txt", "second"), ("deep/x/y.txt", "y"));

        IZipStore zip = await IStore.FromZip(Archive);

        Assert.Equal("second", await zip.ReadText("win/file.txt"));
        Assert.True(await zip.IsFolder("deep/x"));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("./c.txt")]
    [InlineData("a//b.txt")]
    [InlineData("..\\evil.txt")]
    public async Task FromZip_RefusesEntriesThatWouldLeaveTheArchive(string name)
    {
        Write(Archive, ("fine.txt", "ok"), (name, "bad"));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => IStore.FromZip(Archive));

        Assert.Contains("is not a path inside the archive", exception.Message);
    }

    [Fact]
    public async Task FromZip_OfAFileThatIsNotAnArchive_Throws()
    {
        await File.WriteAllTextAsync(Archive, "this is not a zip");

        await Assert.ThrowsAsync<InvalidDataException>(() => IStore.FromZip(Archive));
    }

    [Fact]
    public async Task ReadingAndListingDoNotCountAsChangesButWritingDoes()
    {
        Write(Archive, ("a.txt", "A"));
        IZipStore zip = await IStore.FromZip(Archive);

        await zip.ReadText("a.txt");
        await zip.List(string.Empty).ToListAsync();
        await zip.IsFolder("a.txt");
        await zip.GetSize("a.txt");
        Assert.False(zip.HasChanges);

        await using (await zip.Open("a.txt", StoreOpenMode.Open))
        {
        }

        Assert.True(zip.HasChanges);
    }

    [Fact]
    public async Task MoveCopyAndDelete_WorkOnFoldersLikeAnyStoreAndCountAsChanges()
    {
        Write(Archive, ("tree/a.txt", "A"), ("tree/empty/", null));
        IZipStore zip = await IStore.FromZip(Archive);

        await zip.Copy("tree", "copy");
        await zip.Move("tree", "moved");
        await zip.Delete("copy/a.txt");
        await zip.Save();

        Assert.Equal(["copy/empty/", "moved/a.txt", "moved/empty/"], Names(Archive));
    }

    [Fact]
    public async Task Save_WithoutAFile_Throws()
    {
        using MemoryStream source = new();
        using (ZipArchive archive = new(source, ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("a.txt");
        }

        source.Position = 0;
        IZipStore zip = await IStore.FromZip(source);

        Assert.Null(zip.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => zip.Save());
    }

    [Fact]
    public async Task FromZipOfAStream_ReadsItAndSaveWritesAnArchiveToAnotherStream()
    {
        using MemoryStream source = new();
        using (ZipArchive archive = new(source, ZipArchiveMode.Create, true))
        {
            using StreamWriter writer = new(archive.CreateEntry("dir/a.txt").Open());
            writer.Write("from a stream");
        }

        source.Position = 0;
        IZipStore zip = await IStore.FromZip(source);
        await zip.WriteText("dir/b.txt", "more");
        using MemoryStream target = new();

        await zip.Save(target);

        target.Position = 0;
        using ZipArchive saved = new(target, ZipArchiveMode.Read);
        Assert.Equal(["dir/a.txt", "dir/b.txt"], saved.Entries.Select(entry => entry.FullName).Order());
        Assert.True(source.CanRead);
        IZipStore reopened = await IStore.FromZip(new MemoryStream(target.ToArray()));
        Assert.Equal("from a stream", await reopened.ReadText("dir/a.txt"));
    }

    [Fact]
    public async Task Save_WhenItFails_KeepsTheOldArchiveAndTheChanges()
    {
        Write(Archive, ("a.txt", "old"));
        IZipStore zip = await IStore.FromZip(Archive);
        await zip.WriteText("a.txt", "new");
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => zip.Save(cancellation.Token));

        Assert.True(zip.HasChanges);
        Assert.Equal("old", Text(Archive, "a.txt"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task Save_CreatesTheFolderOfTheArchive()
    {
        IZipStore zip = await IStore.FromZip(temp.Combine("new/folder/out.zip"));
        await zip.WriteText("a.txt", "A");

        await zip.Save();

        Assert.Equal(["a.txt"], Names(temp.Combine("new/folder/out.zip")));
    }

    [Fact]
    public async Task ItWorksAsAMountWithCopiesToAndFromADiskFolder()
    {
        Write(Archive, ("docs/guide.md", "the guide"));
        IZipStore zip = await IStore.FromZip(Archive);
        Directory.CreateDirectory(temp.Combine("project"));
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["zip"] = zip, ["project"] = IStore.FromDisk(temp.Combine("project")) });

        await mounted.Copy("zip/docs", "project/docs");
        await mounted.WriteText("project/notes.txt", "notes");
        await mounted.Move("project/notes.txt", "zip/notes.txt");

        Assert.Equal("the guide", await File.ReadAllTextAsync(temp.Combine("project/docs/guide.md")));
        Assert.Equal("notes", await zip.ReadText("notes.txt"));
        Assert.False(File.Exists(temp.Combine("project/notes.txt")));
        Assert.True(zip.HasChanges);
    }

    [Fact]
    public async Task Save_AfterOtherSavesInParallel_NeverCorruptsTheArchive()
    {
        IZipStore zip = await IStore.FromZip(Archive);
        await zip.WriteText("a.txt", "A");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => zip.Save()));

        Assert.Equal("A", Text(Archive, "a.txt"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
}
