namespace Markwardt.LiveAi.Tests.Unit;

public sealed class ToolEndpointExtensionsTests
{
    private PromptSessionRecord Record(Action<PromptSessionRecordBuilder>? change = null)
    {
        PromptSessionRecordBuilder builder = new();
        change?.Invoke(builder);
        return builder.Build(new ClaudeEngine());
    }

    private ToolEndpoint Endpoint(bool hasShell = false)
        => new() { Url = "http://127.0.0.1:1/mcp", Token = "t", Names = ["x"], HasShell = hasShell, CacheName = "CACHE:" };

    [Fact]
    public void Guidance_ForFileToolsWithoutAShell_TellsTheAiItsPathsAreRelativeToItsWorkspaceExceptTheCache()
    {
        string guidance = Endpoint().Guidance(Record());

        Assert.Contains("file tools", guidance);
        Assert.Contains("relative to your workspace", guidance);
        Assert.Contains("CACHE:", guidance);
        Assert.DoesNotContain("is empty", guidance);
    }

    [Fact]
    public void Guidance_WhenTheToolMaySearchTheWeb_SaysSoOtherwiseNot()
    {
        Assert.Contains("search the web with your own web search tool", Endpoint().Guidance(Record() with { NativeSearch = true }));
        Assert.Contains("search the web with your own web search tool", ((ToolEndpoint?)null).Guidance(Record() with { NativeSearch = true }));
        Assert.DoesNotContain("search the web", Endpoint().Guidance(Record()));
    }

    [Fact]
    public void Guidance_WithoutToolsSaysThereAreNone()
        => Assert.Contains("You have no tools", ((ToolEndpoint?)null).Guidance(Record()));

    [Fact]
    public void Guidance_WithTheShellAndFileToolsMentionsBothAndTheCache()
    {
        string text = Endpoint(true).Guidance(Record());

        Assert.Contains("a shell tool and file tools", text);
        Assert.Contains("unless it starts with CACHE:", text);
        Assert.Contains("The shell tool starts in your workspace.", text);
        Assert.DoesNotContain("no shell", text);
    }

    [Fact]
    public void Equals_ComparesTheShellFlagToo()
        => Assert.NotEqual(Endpoint(false), Endpoint(true));

    [Fact]
    public void Guidance_WithFileTools_PointsAtThemAndAtTheWorkspaceAndTheCache()
    {
        string text = Endpoint().Guidance(Record());

        Assert.Contains("no shell", text);
        Assert.Contains("relative to your workspace", text);
        Assert.Contains("file tools of the MCP server named liveai", text);
        Assert.Contains("unless it starts with CACHE:", text);
    }

    [Fact]
    public void Guidance_WithExternalServersNamesThemAndAllowsTheirTools()
    {
        PromptSessionRecord record = Record(builder => builder.McpServers = new Dictionary<string, PromptMcpServer> { ["db"] = PromptMcpServer.Http("http://localhost:1/mcp"), ["git"] = PromptMcpServer.Stdio("git-mcp") });

        Assert.Contains("MCP servers named db, git", ((ToolEndpoint?)null).Guidance(record));
        Assert.DoesNotContain("You have no tools", ((ToolEndpoint?)null).Guidance(record));
        Assert.Contains("MCP servers named db, git", Endpoint().Guidance(record));
        Assert.Contains("MCP servers named db, git", Endpoint(true).Guidance(record));
    }

    [Fact]
    public void Guidance_WithInstructionsAppendsThemAfterTheAccessText()
    {
        string text = Endpoint().Guidance(Record(builder => builder.Instructions = "Answer in French."));

        Assert.EndsWith("\n\nInstructions from the user:\nAnswer in French.", text);
        Assert.StartsWith("You have no shell", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Guidance_WithoutRealInstructionsAddsNothing(string? instructions)
        => Assert.DoesNotContain("Instructions from the user", ((ToolEndpoint?)null).Guidance(Record(builder => builder.Instructions = instructions)));
}
