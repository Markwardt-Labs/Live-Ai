namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionOpenerTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IPromptEngine> engine = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptSession Open(PromptSessionLink link)
    {
        Mock<IPromptLibraryTools> libraryTools = new();
        libraryTools.SetupGet(mock => mock.Names).Returns(["read_file"]);
        libraryTools.SetupGet(mock => mock.ShellName).Returns("shell");
        libraryTools.OfferTheEverydayTools();
        return new PromptSessionOpener(new Mock<IPromptSessionStore>().Object, new Mock<ITurnRunner>().Object, new Mock<IPromptToolHost>().Object, libraryTools.Object).Open(link, new Mock<IPromptProvider>().Object, engine.Object, null);
    }

    private PromptSessionLink Link(string? workspace = null, bool sandbox = true)
        => new() { Id = Guid.NewGuid(), Provider = "Fake", WorkingFolder = workspace ?? temp.Path, WorkspacePath = workspace, IsSandboxed = sandbox };

    [Fact]
    public void ALinkWithAWorkspace_OpensASessionWhoseWorkspaceIsThatFolder()
    {
        IPromptSession session = Open(Link(temp.Path));

        Assert.Equal(temp.Path, session.WorkspacePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheSandboxOfTheLink_IsTheOneOfTheWorkspace(bool sandbox)
    {
        using TempFolder other = new();
        string outside = other.Combine("outside.txt");
        await File.WriteAllTextAsync(outside, "secret");
        IPromptSession session = Open(Link(temp.Path, sandbox));

        Task<string> read = ((PromptSession)session).Files.ReadText(outside);

        if (sandbox)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => read);
        }
        else
        {
            Assert.Equal("secret", await read);
        }
    }

    [Fact]
    public void ALinkWithoutAWorkspace_OpensASessionWorkingInItsWorkingFolder()
        => Assert.Equal(temp.Path, Open(Link()).WorkspacePath);

    [Fact]
    public void ALinkWhoseWorkspaceIsGone_Throws()
        => Assert.Throws<DirectoryNotFoundException>(() => Open(Link(Path.Combine(temp.Path, "gone"))));
}
