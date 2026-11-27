namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class ReadAccessTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly string workspace;

    private readonly string shared;

    public ReadAccessTests()
    {
        workspace = Directory.CreateDirectory(temp.Combine("workspace")).FullName;
        shared = Directory.CreateDirectory(temp.Combine("shared")).FullName;
        Directory.CreateDirectory(Path.Combine(shared, "sub"));
        File.WriteAllText(Path.Combine(shared, "note.txt"), "from the other side");
        File.WriteAllText(Path.Combine(shared, "sub", "tool.sh"), "#!/bin/sh\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(shared, "sub", "tool.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
        => temp.Dispose();

    // The tools of a prompt that is running hold the store they were made with, which is what has to see a grant made after it.
    private async Task<(PromptSession Session, IStore Tools)> Open(bool everything = false)
    {
        IPromptSession session = temp.Track(await TestServices.Make(new ScriptEngine("unused"), temp.Combine("data")).Create(new PromptToolServiceOptions { WorkspacePath = workspace }));
        session.Configuration.IsSystemAccessAllowed = everything;
        return ((PromptSession)session, ((PromptSession)session).Files);
    }

    [Fact]
    public async Task AGrantedFolderIsReadByAStoreMadeBeforeAndNoLongerOnceTheGrantEnds()
    {
        (PromptSession session, IStore tools) = await Open();
        await using PromptSession owned = session;
        string note = Path.Combine(shared, "note.txt");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadText(note));

        IDisposable grant = session.GrantReadAccess(shared);

        Assert.Equal("from the other side", await tools.ReadText(note));
        Assert.Equal("from the other side", await tools.ReadText("../shared/note.txt"));
        Assert.Equal(["note.txt", "sub/tool.sh"], (await tools.ListFiles(shared, true).ToListAsync()).Order(StringComparer.Ordinal));
        Assert.True(await tools.IsFolder(Path.Combine(shared, "sub")));
        Assert.Equal("from the other side".Length, await tools.GetSize(note));

        grant.Dispose();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadText(note));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ListFiles(shared, true).ToListAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NothingIsWrittenIntoAGrantedFolderEvenThroughALinkOrWithAccessToTheWholeMachine(bool everything)
    {
        (PromptSession session, IStore tools) = await Open(everything);
        await using PromptSession owned = session;
        tools = session.Files;
        using IDisposable grant = session.GrantReadAccess(shared);
        string note = Path.Combine(shared, "note.txt");
        Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), shared);
        await tools.WriteText("mine.txt", "mine");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.WriteText(note, "changed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.WriteText("link/note.txt", "changed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.WriteText(Path.Combine(shared, "new.txt"), "new"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.CreateFolder(Path.Combine(shared, "made")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Delete(note));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Move(note, "taken.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Move("mine.txt", Path.Combine(shared, "mine.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Copy("mine.txt", Path.Combine(shared, "mine.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Delete(temp.Path));

        Assert.Equal("from the other side", await File.ReadAllTextAsync(note));
        Assert.Equal(["note.txt", "sub"], Directory.GetFileSystemEntries(shared).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FilesAndFoldersAreCopiedOutWithScriptsStillRunnable()
    {
        (PromptSession session, IStore tools) = await Open();
        await using PromptSession owned = session;
        using IDisposable grant = session.GrantReadAccess(shared);

        await tools.Copy(Path.Combine(shared, "note.txt"), "docs/note.txt");
        await tools.Copy(Path.Combine(shared, "sub"), "copied");

        Assert.Equal("from the other side", await File.ReadAllTextAsync(Path.Combine(workspace, "docs", "note.txt")));
        Assert.True(File.Exists(Path.Combine(workspace, "copied", "tool.sh")));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(workspace, "copied", "tool.sh")).HasFlag(UnixFileMode.UserExecute));
        }

        await Assert.ThrowsAsync<IOException>(() => tools.Copy(Path.Combine(shared, "note.txt"), "docs/note.txt"));
    }

    [Fact]
    public async Task ALinkInAGrantedFolderThatLeadsOutOfItIsNotFollowed()
    {
        File.WriteAllText(temp.Combine("secret.txt"), "secret");
        File.CreateSymbolicLink(Path.Combine(shared, "escape.txt"), temp.Combine("secret.txt"));
        (PromptSession session, IStore tools) = await Open();
        await using PromptSession owned = session;
        using IDisposable grant = session.GrantReadAccess(shared);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadText(Path.Combine(shared, "escape.txt")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Copy(Path.Combine(shared, "escape.txt"), "stolen.txt"));
        Assert.False(File.Exists(Path.Combine(workspace, "stolen.txt")));
    }

    [Fact]
    public async Task AFolderThatIsGrantedTwiceStaysUntilBothGrantsEndAndAMissingOneIsRefused()
    {
        (PromptSession session, IStore tools) = await Open();
        await using PromptSession owned = session;
        IDisposable first = session.GrantReadAccess(shared);
        IDisposable second = session.GrantReadAccess(shared);

        first.Dispose();
        first.Dispose();

        Assert.Equal("from the other side", await tools.ReadText(Path.Combine(shared, "note.txt")));
        second.Dispose();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadText(Path.Combine(shared, "note.txt")));
        Assert.Throws<DirectoryNotFoundException>(() => session.GrantReadAccess(temp.Combine("missing")));
    }

    [Fact]
    public async Task ACopyOfAGrantedFolderThatFailsLeavesNothingBehind()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (PromptSession session, IStore tools) = await Open();
        await using PromptSession owned = session;
        using IDisposable grant = session.GrantReadAccess(shared);
        File.WriteAllText(temp.Combine("outside.txt"), "not shared");
        File.CreateSymbolicLink(Path.Combine(shared, "sub", "zz-escape.txt"), temp.Combine("outside.txt"));

        await Assert.ThrowsAnyAsync<Exception>(() => tools.Copy(shared, "copy"));

        Assert.False(Directory.Exists(Path.Combine(workspace, "copy")));
    }

    [Theory]
    [InlineData(StringComparison.Ordinal, false)]
    [InlineData(StringComparison.OrdinalIgnoreCase, true)]
    public void AGrantMatchesAnotherCaseOnlyWhenPathsAreComparedWithoutIt(StringComparison comparison, bool matches)
    {
        ReadGrants grants = new(comparison);
        using IDisposable grant = grants.Add(shared);

        Assert.Equal(matches, grants.Containing(Path.Combine(shared.ToUpperInvariant(), "a.txt")) is not null);
        Assert.Equal(matches, grants.Holds(Path.GetDirectoryName(shared)!.ToUpperInvariant()));
    }
}
