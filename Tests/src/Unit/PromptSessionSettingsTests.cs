namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionSettingsTests
{
    private int changes;

    private PromptSessionSettings Settings(PromptSessionRecord? record = null)
    {
        PromptSessionSettings settings = new(record ?? new PromptSessionRecordBuilder().Build(new ClaudeEngine()), ["read_file", "shell"]);
        settings.Changed += () => Interlocked.Increment(ref changes);
        return settings;
    }

    private PromptTool Tool(string name)
        => new() { Name = name, Description = "d", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement, Handler = (_, _) => Task.FromResult("ok") };

    [Fact]
    public void ItStartsWithWhatIsStored()
    {
        PromptSessionRecord record = new PromptSessionRecordBuilder { Instructions = "Be brief.", McpServers = new Dictionary<string, PromptMcpServer> { ["db"] = PromptMcpServer.Http("https://example.com") } }.Build(new CodexEngine()) with { Capability = PromptCapability.Strong, Effort = PromptEffort.High, IsImageGenerationAllowed = true };

        PromptSessionSettings settings = Settings(record);

        Assert.Equal((PromptCapability.Strong, PromptEffort.High, false, "Be brief."), (settings.Capability, settings.Effort, settings.IsShellAllowed, settings.Instructions));
        Assert.Equal(["db"], settings.McpServers.Keys);
        Assert.Empty(settings.Tools);
        Assert.Null(settings.Asker);
        Assert.True(settings.IsImageGenerationAllowed);
    }

    [Fact]
    public void EverySetterTellsTheOwnerOnce()
    {
        PromptSessionSettings settings = Settings();

        settings.Capability = PromptCapability.Strong;
        settings.Effort = PromptEffort.Low;
        settings.IsShellAllowed = true;
        settings.IsSystemAccessAllowed = true;
        settings.IsWebAllowed = true;
        settings.Asker = (_, _) => Task.FromResult(string.Empty);
        settings.IsImageGenerationAllowed = true;
        settings.Instructions = "y";

        Assert.Equal(8, changes);
    }

    [Fact]
    public void TheImageGenerationSwitchIsPartOfTheSnapshotAndCopiedToAnotherConfiguration()
    {
        PromptSessionSettings settings = Settings();
        PromptSessionSettings other = Settings();
        settings.IsImageGenerationAllowed = true;

        settings.Snapshot().CopyTo(other);

        Assert.True(settings.Snapshot().IsImageGenerationAllowed);
        Assert.True(other.IsImageGenerationAllowed);
    }

    [Fact]
    public void TheAskerIsPartOfTheSnapshotAndIsCopiedToAnotherConfiguration()
    {
        PromptSessionSettings settings = Settings();
        PromptSessionSettings other = Settings();
        PromptAsker asker = (_, _) => Task.FromResult("yes");

        settings.Asker = asker;
        settings.Snapshot().CopyTo(other);

        Assert.Same(asker, settings.Snapshot().Asker);
        Assert.Same(asker, other.Asker);
    }

    [Fact]
    public void ABadValueThrowsAtOnceChangesNothingAndTellsNoOne()
    {
        PromptSessionSettings settings = Settings();

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Capability = (PromptCapability)99);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Effort = (PromptEffort)99);

        Assert.Equal(0, changes);
        Assert.Equal((PromptCapability.Balanced, PromptEffort.High), (settings.Capability, settings.Effort));
    }

    [Fact]
    public void InstructionsAreKeptAsWrittenAndBlankMeansNone()
    {
        PromptSessionSettings settings = Settings();

        settings.Instructions = "  keep my spacing ";

        Assert.Equal("  keep my spacing ", settings.Instructions);
        settings.Instructions = "";
        Assert.Null(settings.Instructions);
        settings.Instructions = "   ";
        Assert.Null(settings.Instructions);
    }

    [Fact]
    public void ToolsBehaveLikeAListThatChecksNamesAndTellsTheOwner()
    {
        PromptSessionSettings settings = Settings();
        PromptTool alpha = Tool("alpha");
        PromptTool beta = Tool("beta");
        PromptTool gamma = Tool("gamma");

        settings.Tools.Add(alpha);
        settings.Tools.Insert(0, beta);
        settings.Tools[1] = gamma;

        Assert.Equal([beta, gamma], settings.Tools);
        Assert.Equal(2, settings.Tools.Count);
        Assert.Contains(gamma, settings.Tools);
        Assert.Equal(1, settings.Tools.IndexOf(gamma));
        Assert.False(settings.Tools.IsReadOnly);
        PromptTool[] copy = new PromptTool[3];
        settings.Tools.CopyTo(copy, 1);
        Assert.Null(copy[0]);
        Assert.Equal([beta, gamma], copy.Skip(1));
        Assert.True(settings.Tools.Remove(beta));
        Assert.False(settings.Tools.Remove(beta));
        settings.Tools.RemoveAt(0);
        settings.Tools.Clear();
        settings.Tools.Clear();
        Assert.Empty(settings.Tools);
        Assert.Equal(5, changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("read_file")]
    [InlineData("shell")]
    [InlineData("Shell")]
    public void AToolWithANameThatIsNotAllowedIsRefused(string name)
    {
        PromptSessionSettings settings = Settings();

        Assert.Throws<ArgumentException>(() => settings.Tools.Add(Tool(name)));
        Assert.Throws<ArgumentException>(() => settings.Tools.Insert(0, Tool(name)));
        Assert.Empty(settings.Tools);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ReplacingAToolByIndexMayKeepItsOwnNameButNotTakeAnothers()
    {
        PromptSessionSettings settings = Settings();
        settings.Tools.Add(Tool("alpha"));
        settings.Tools.Add(Tool("beta"));

        settings.Tools[0] = Tool("alpha");
        Assert.Throws<ArgumentException>(() => settings.Tools[0] = Tool("beta"));
        Assert.Throws<ArgumentException>(() => settings.Tools.Add(Tool("alpha")));
    }

    [Fact]
    public void EnumeratingAListOrADictionaryWalksACopyThatLaterChangesDoNotDisturb()
    {
        PromptSessionSettings settings = Settings();
        settings.Tools.Add(Tool("alpha"));
        settings.McpServers["docs"] = PromptMcpServer.Http("https://example.com");

        foreach (PromptTool tool in settings.Tools)
        {
            settings.Tools.Add(Tool(tool.Name + "2"));
        }

        foreach (KeyValuePair<string, PromptMcpServer> server in settings.McpServers)
        {
            settings.McpServers.Remove(server.Key);
        }

        Assert.Equal(["alpha", "alpha2"], settings.Tools.Select(tool => tool.Name));
        Assert.Empty(settings.McpServers);
    }

    [Fact]
    public void AGuardedDictionaryBehavesLikeADictionaryThatChecksNamesAndTellsTheOwner()
    {
        PromptSessionSettings settings = Settings();
        PromptMcpServer first = PromptMcpServer.Http("https://example.com/1");
        PromptMcpServer second = PromptMcpServer.Http("https://example.com/2");

        settings.McpServers["a"] = first;
        settings.McpServers.Add("b", second);
        settings.McpServers.Add(new KeyValuePair<string, PromptMcpServer>("c", first));
        settings.McpServers["a"] = second;

        Assert.Equal(["a", "b", "c"], settings.McpServers.Keys.Order());
        Assert.Equal(3, settings.McpServers.Count);
        Assert.Contains(second, settings.McpServers.Values);
        Assert.True(settings.McpServers.ContainsKey("b"));
        Assert.True(settings.McpServers.TryGetValue("a", out PromptMcpServer? found) && found == second);
        Assert.False(settings.McpServers.TryGetValue("missing", out _));
        Assert.Contains(new KeyValuePair<string, PromptMcpServer>("a", second), settings.McpServers);
        Assert.False(settings.McpServers.IsReadOnly);
        KeyValuePair<string, PromptMcpServer>[] copy = new KeyValuePair<string, PromptMcpServer>[4];
        settings.McpServers.CopyTo(copy, 1);
        Assert.Contains(copy, entry => entry.Key == "b");
        Assert.True(settings.McpServers.Remove("b"));
        Assert.False(settings.McpServers.Remove("b"));
        Assert.True(settings.McpServers.Remove(new KeyValuePair<string, PromptMcpServer>("c", first)));
        Assert.False(settings.McpServers.Remove(new KeyValuePair<string, PromptMcpServer>("c", first)));
        settings.McpServers.Clear();
        settings.McpServers.Clear();
        settings.McpServers.Add("a", first);
        Assert.Throws<ArgumentException>(() => settings.McpServers.Add("a", first));
        Assert.Equal(8, changes);
    }

    [Fact]
    public void ServersAreCheckedWhenTheyAreAdded()
    {
        PromptSessionSettings settings = Settings();

        settings.McpServers["db"] = PromptMcpServer.Http("https://example.com");
        settings.McpServers["git"] = PromptMcpServer.Stdio("git-mcp");

        Assert.Throws<ArgumentException>(() => settings.McpServers["liveai"] = PromptMcpServer.Http("https://example.com"));
        Assert.Throws<ArgumentException>(() => settings.McpServers["bad name"] = PromptMcpServer.Http("https://example.com"));
        Assert.Throws<ArgumentException>(() => settings.McpServers["none"] = new PromptMcpServer());
        Assert.Throws<ArgumentException>(() => settings.McpServers["both"] = new PromptMcpServer { Command = "x", Url = "https://example.com" });
        Assert.Throws<ArgumentException>(() => settings.McpServers["ftp"] = PromptMcpServer.Http("ftp://example.com"));
        Assert.Throws<ArgumentException>(() => settings.McpServers["blank"] = PromptMcpServer.Stdio("  "));
        Assert.Equal(["db", "git"], settings.McpServers.Keys.Order());
    }

    [Fact]
    public void ASnapshotIsACopyThatLaterChangesDoNotReach()
    {
        PromptSessionSettings settings = Settings();
        settings.Capability = PromptCapability.Strong;
        settings.Tools.Add(Tool("alpha"));
        settings.McpServers["db"] = PromptMcpServer.Http("https://example.com");

        PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
        settings.Capability = PromptCapability.Fast;
        settings.Tools.Clear();
        settings.McpServers.Clear();

        Assert.Equal((PromptCapability.Strong, PromptEffort.High), (snapshot.Capability, snapshot.Effort));
        Assert.Equal(["alpha"], snapshot.Tools.Select(tool => tool.Name));
        Assert.Equal(["db"], snapshot.McpServers.Keys);
    }

    [Fact]
    public void AfterItIsClosedEveryChangeThrowsButReadingStillWorks()
    {
        PromptSessionSettings settings = Settings();
        settings.Tools.Add(Tool("alpha"));
        settings.Close();

        Assert.Throws<ObjectDisposedException>(() => settings.Instructions = "x");
        Assert.Throws<ObjectDisposedException>(() => settings.Effort = PromptEffort.High);
        Assert.Throws<ObjectDisposedException>(() => settings.Tools.Add(Tool("beta")));
        Assert.Throws<ObjectDisposedException>(() => settings.Tools.Clear());
        Assert.Throws<ObjectDisposedException>(() => settings.McpServers.Clear());
        Assert.Equal(["alpha"], settings.Snapshot().Tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task SettingsCanBeChangedFromManyThreadsAtOnce()
    {
        PromptSessionSettings settings = Settings();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int number = 0; number < 50; number++)
            {
                settings.Tools.Add(Tool($"tool-{worker}-{number}"));
                settings.McpServers[$"server-{worker}-{number}"] = PromptMcpServer.Http("https://example.com");
                _ = settings.Snapshot();
            }
        })));

        Assert.Equal(400, settings.Tools.Count);
        Assert.Equal(400, settings.McpServers.Count);
    }

}
