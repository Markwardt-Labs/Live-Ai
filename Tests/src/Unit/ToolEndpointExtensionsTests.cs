namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ToolEndpointExtensionsTests
{
    private PromptSessionRecord Record(Action<PromptSessionRecordBuilder>? change = null)
    {
        PromptSessionRecordBuilder builder = new();
        change?.Invoke(builder);
        return builder.Build(new ClaudeEngine());
    }

    private ToolEndpoint Endpoint(bool hasFiles, bool hasShell = false, bool hasWorkspace = false, bool shellInWorkspace = false)
        => new() { Url = "http://127.0.0.1:1/mcp", Token = "t", Names = ["x"], HasFiles = hasFiles, HasShell = hasShell, HasWorkspace = hasWorkspace, ShellInWorkspace = shellInWorkspace };

    [Fact]
    public void Guidance_ForFileToolsWithoutAShell_SaysTheFolderItRunsInIsEmptyOnlyWithoutAWorkspaceFolder()
    {
        PromptSessionRecord withWorkspace = Record() with { WorkspacePath = "/project" };

        Assert.Contains("the folder you run in is empty and is not your workspace", Endpoint(true, hasWorkspace: true).Guidance(Record()));
        Assert.DoesNotContain("is empty", Endpoint(true, hasWorkspace: true).Guidance(withWorkspace));
        Assert.Contains("file tools", Endpoint(true, hasWorkspace: true).Guidance(withWorkspace));
    }

    [Fact]
    public void Guidance_WhenTheToolMaySearchTheWeb_SaysSoOtherwiseNot()
    {
        Assert.Contains("search the web with your own web search tool", Endpoint(true).Guidance(Record() with { NativeSearch = true }));
        Assert.Contains("search the web with your own web search tool", ((ToolEndpoint?)null).Guidance(Record() with { NativeSearch = true }));
        Assert.DoesNotContain("search the web", Endpoint(true).Guidance(Record()));
    }

    [Fact]
    public void Guidance_WithoutToolsSaysThereAreNone()
        => Assert.Contains("You have no tools", ((ToolEndpoint?)null).Guidance(Record()));

    [Fact]
    public void Guidance_WithOnlyTheCallersToolsSaysThereAreNoFiles()
    {
        string text = Endpoint(false).Guidance(Record());

        Assert.Contains("no access to files", text);
        Assert.Contains("MCP server named liveai", text);
    }

    [Fact]
    public void Guidance_WithOnlyTheShellSaysThatAndThatThereAreNoOtherFiles()
    {
        string text = Endpoint(false, true).Guidance(Record());

        Assert.Contains("run commands on this machine only with the shell tool", text);
        Assert.Contains("working_directory", text);
        Assert.Contains("no other access to files", text);
        Assert.DoesNotContain("no shell", text);
    }

    [Fact]
    public void Guidance_WithTheShellAndFileToolsMentionsBothAndTheMountedFolders()
    {
        string text = Endpoint(true, true).Guidance(Record());

        Assert.Contains("a shell tool and file tools", text);
        Assert.Contains("starts with the path of a mounted folder", text);
        Assert.Contains("empty scratch folder that is not your workspace", text);
        Assert.DoesNotContain("no shell", text);
    }

    [Fact]
    public void Guidance_WithAWorkspace_SaysPathsAreRelativeToItAndWhereTheShellStarts()
    {
        string inScratch = Endpoint(true, true, hasWorkspace: true).Guidance(Record());
        string inWorkspace = Endpoint(true, true, hasWorkspace: true, shellInWorkspace: true).Guidance(Record());
        string filesOnly = Endpoint(true, false, hasWorkspace: true).Guidance(Record());

        Assert.Contains("relative to your workspace", inScratch);
        Assert.Contains("empty scratch folder", inScratch);
        Assert.Contains("The shell tool starts in your workspace.", inWorkspace);
        Assert.DoesNotContain("scratch", inWorkspace);
        Assert.Contains("list_directory with no path shows the workspace", filesOnly);
        Assert.DoesNotContain("shows the mounted folders", filesOnly);
    }

    [Fact]
    public void Equals_ComparesTheWorkspaceFlagsToo()
    {
        Assert.NotEqual(Endpoint(true), Endpoint(true, hasWorkspace: true));
        Assert.NotEqual(Endpoint(true, true, true), Endpoint(true, true, true, true));
    }

    [Fact]
    public void Equals_ComparesTheShellFlagToo()
        => Assert.NotEqual(Endpoint(true, false), Endpoint(true, true));

    [Fact]
    public void Guidance_WithFileToolsPointsAtThemAndAwayFromTheFolderItRunsIn()
    {
        string text = Endpoint(true).Guidance(Record());

        Assert.Contains("no shell", text);
        Assert.Contains("folder you run in is empty", text);
        Assert.Contains("file tools of the MCP server named liveai", text);
        Assert.Contains("starts with the path of a mounted folder", text);
    }

    [Fact]
    public void Guidance_WithExternalServersNamesThemAndAllowsTheirTools()
    {
        PromptSessionRecord record = Record(builder => builder.McpServers = new Dictionary<string, PromptMcpServer> { ["db"] = PromptMcpServer.Http("http://localhost:1/mcp"), ["git"] = PromptMcpServer.Stdio("git-mcp") });

        Assert.Contains("MCP servers named db, git", ((ToolEndpoint?)null).Guidance(record));
        Assert.DoesNotContain("You have no tools", ((ToolEndpoint?)null).Guidance(record));
        Assert.Contains("MCP servers named db, git", Endpoint(true).Guidance(record));
        Assert.Contains("MCP servers named db, git", Endpoint(false).Guidance(record));
        Assert.Contains("MCP servers named db, git", Endpoint(false, true).Guidance(record));
        Assert.Contains("MCP servers named db, git", Endpoint(true, true).Guidance(record));
    }

    [Fact]
    public void Guidance_WithInstructionsAppendsThemAfterTheAccessText()
    {
        string text = Endpoint(true).Guidance(Record(builder => builder.Instructions = "Answer in French."));

        Assert.EndsWith("\n\nInstructions from the user:\nAnswer in French.", text);
        Assert.StartsWith("You have no shell", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Guidance_WithoutRealInstructionsAddsNothing(string? instructions)
        => Assert.DoesNotContain("Instructions from the user", ((ToolEndpoint?)null).Guidance(Record(builder => builder.Instructions = instructions)));

    [Fact]
    public void Equality_ConsidersWhetherThereAreFiles()
        => Assert.NotEqual(Endpoint(true), Endpoint(false));
}
