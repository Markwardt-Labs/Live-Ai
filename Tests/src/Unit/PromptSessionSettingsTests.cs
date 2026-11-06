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
        PromptSessionRecord record = new PromptSessionRecordBuilder { Instructions = "Be brief.", McpServers = new Dictionary<string, PromptMcpServer> { ["db"] = PromptMcpServer.Http("https://example.com") } }.Build(new CodexEngine()) with { ModelSettings = new Dictionary<string, string> { ["effort"] = "high" } };

        PromptSessionSettings settings = Settings(record);

        Assert.Equal(("sonnet", "high", false, false, "Be brief."), (settings.Model!.Id, settings.ModelSettings["effort"], settings.IsPersistent, settings.EnableShell, settings.Instructions));
        Assert.Equal(["db"], settings.McpServers.Keys);
        Assert.Empty(settings.Tools);
        Assert.Null(settings.Limits.Budget);
        Assert.Null(settings.Workspace);
        Assert.Equal((null, null, null, null, null), (settings.BeforeToolCall, settings.AfterToolCall, settings.Asker, settings.WebFilter, settings.ImageGenerator));
        Assert.Equal((null, null, null, null), (settings.Limits.Timeout, settings.Limits.WindowLimit, settings.Limits.Retry, settings.Limits.MaxToolCalls));
    }

    [Fact]
    public void ChangingTheModelKeepsTheValuesBothModelsHaveAndGivesTheRestTheirDefaults()
    {
        PromptSessionSettings settings = Settings();
        settings.ModelSettings["effort"] = "high";
        PromptModel other = new() { Id = "other", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"]), PromptModelSetting.Count("context", "Context window", 1024, 8192, 4096)] };

        settings.Model = other;

        Assert.Equal(["context", "effort"], settings.ModelSettings.Keys.Order());
        Assert.Equal(("high", "4096"), (settings.ModelSettings["effort"], settings.ModelSettings["context"]));
    }

    [Fact]
    public void AValueTheNewModelDoesNotAcceptGoesBackToItsDefaultAndASettingItLacksIsDropped()
    {
        PromptSessionSettings settings = Settings();
        settings.ModelSettings["effort"] = "max";
        PromptModel other = new() { Id = "other", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "high"], "low")] };

        settings.Model = other;
        settings.Model = new PromptModel { Id = "plain", Settings = [] };

        Assert.Empty(settings.ModelSettings);
    }

    [Fact]
    public void ARecordThatAnEffortOfTheOldModelIsNotAcceptedByTheNewOneFallsBack()
    {
        PromptSessionSettings settings = Settings();
        settings.ModelSettings["effort"] = "max";

        settings.Model = new PromptModel { Id = "other", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "high"], "low")] };

        Assert.Equal("low", settings.ModelSettings["effort"]);
    }

    [Fact]
    public void WithoutAModelNoSettingCanBeSetAndSelectingNoneClearsThem()
    {
        PromptSessionSettings settings = Settings();

        settings.Model = null;

        Assert.Empty(settings.ModelSettings);
        Assert.Throws<PromptSessionModelException>(() => settings.ModelSettings["effort"] = "low");
    }

    [Fact]
    public void AModelWhoseSettingsAreNotKnownTakesTheValuesAsTheyAre()
    {
        PromptSessionSettings settings = Settings();

        settings.Model = new PromptModel { Id = "stored" };
        settings.ModelSettings["anything"] = "goes";

        Assert.Equal("goes", settings.ModelSettings["anything"]);
        Assert.Equal("medium", settings.ModelSettings["effort"]);
    }

    [Fact]
    public void EverySetterTellsTheOwnerOnce()
    {
        PromptSessionSettings settings = Settings();

        settings.Model = TestModels.Model;
        settings.ModelSettings["effort"] = "low";
        settings.IsPersistent = true;
        settings.EnableShell = true;
        settings.EnableImages = true;
        settings.EnableWeb = true;
        settings.Workspace = IStore.FromMemory();
        settings.BeforeToolCall = (_, _) => Task.FromResult(PromptToolDecision.Allow);
        settings.AfterToolCall = (_, _, _, _) => Task.CompletedTask;
        settings.Asker = (_, _) => Task.FromResult(string.Empty);
        settings.WebFilter = (_, _) => Task.FromResult(false);
        settings.EnableSearch = true;
        settings.ImageGenerator = new FakeImageGenerator();
        settings.Limits = settings.Limits with { Budget = new PromptBudget { MaxTokens = 5 } };
        settings.Limits = settings.Limits with { Timeout = TimeSpan.FromMinutes(1) };
        settings.Limits = settings.Limits with { WindowLimit = new PromptWindowLimit { Reached = _ => false } };
        settings.Limits = settings.Limits with { Retry = new PromptRetry { MaxAttempts = 3 } };
        settings.Limits = settings.Limits with { MaxToolCalls = 1 };
        settings.Instructions = "y";

        Assert.Equal(19, changes);
    }

    [Fact]
    public void TheImageGeneratorIsPartOfTheSnapshotAndCopiedToAnotherConfiguration()
    {
        PromptSessionSettings settings = Settings();
        PromptSessionSettings other = Settings();
        FakeImageGenerator generator = new();
        settings.ImageGenerator = generator;

        settings.Snapshot().CopyTo(other);

        Assert.Same(generator, settings.Snapshot().ImageGenerator);
        Assert.Same(generator, other.ImageGenerator);
    }

    [Fact]
    public void EnableSearchIsOffByDefaultAndIsPartOfTheSnapshotAndCopiedToAnotherConfiguration()
    {
        PromptSessionSettings settings = Settings();
        PromptSessionSettings other = Settings();

        Assert.False(settings.EnableSearch);
        settings.EnableSearch = true;
        settings.Snapshot().CopyTo(other);

        Assert.True(settings.Snapshot().EnableSearch);
        Assert.True(other.EnableSearch);
    }

    [Fact]
    public void TheWebFilterIsPartOfTheSnapshotAndIsCopiedToAnotherConfiguration()
    {
        PromptSessionSettings settings = Settings();
        PromptSessionSettings other = Settings();
        Func<Uri, CancellationToken, Task<bool>> filter = (_, _) => Task.FromResult(true);
        settings.WebFilter = filter;

        settings.Snapshot().CopyTo(other);

        Assert.Same(filter, settings.Snapshot().WebFilter);
        Assert.Same(filter, other.WebFilter);
    }

    [Fact]
    public void TheAskerIsPartOfTheSnapshotAndIsCopiedToAnotherConfiguration()
    {
        PromptSessionSettings settings = Settings();
        PromptSessionSettings other = Settings();
        Func<string, CancellationToken, Task<string>> asker = (_, _) => Task.FromResult("yes");

        settings.Asker = asker;
        settings.Snapshot().CopyTo(other);

        Assert.Same(asker, settings.Snapshot().Asker);
        Assert.Same(asker, other.Asker);
    }

    [Fact]
    public void TheLimitsAreOneRecordThatIsCheckedAsAWhole()
    {
        PromptSessionSettings settings = Settings();

        Assert.Throws<ArgumentNullException>(() => settings.Limits = null!);
        ArgumentException bad = Assert.Throws<ArgumentException>(() => settings.Limits = new PromptSessionLimits { Timeout = TimeSpan.FromSeconds(5), MaxToolCalls = -1 });
        Assert.Contains("tool calls", bad.Message);
        Assert.Equal(new PromptSessionLimits(), settings.Limits);

        PromptSessionLimits limits = new() { Timeout = TimeSpan.FromSeconds(5), MaxToolCalls = 2, Retry = new PromptRetry { MaxAttempts = 2 }, Budget = new PromptBudget { MaxTokens = 9 } };
        settings.Limits = limits;
        PromptRequestLimits request = settings.RequestLimits();

        Assert.Same(limits, settings.Limits);
        Assert.Equal((TimeSpan.FromSeconds(5), 2, 2), (request.Timeout, request.MaxToolCalls, request.Retry!.MaxAttempts));
    }

    [Fact]
    public void ABadValueThrowsAtOnceChangesNothingAndTellsNoOne()
    {
        PromptSessionSettings settings = Settings();

        Assert.Throws<ArgumentException>(() => settings.ModelSettings["effort"] = "extreme");
        Assert.Throws<ArgumentException>(() => settings.ModelSettings["unknown"] = "x");
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Budget = new PromptBudget { MaxTokens = 0 } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Timeout = TimeSpan.Zero });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { WindowLimit = new PromptWindowLimit { Reached = _ => false, Interval = TimeSpan.FromDays(25) } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { WindowLimit = new PromptWindowLimit { Reached = _ => false, Interval = TimeSpan.Zero } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { MaxAttempts = 0 } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { InitialDelay = TimeSpan.FromSeconds(-1) } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { InitialDelay = TimeSpan.FromDays(25) } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { MaxDelay = TimeSpan.FromSeconds(-1) } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { MaxDelay = TimeSpan.FromDays(25) } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { BackoffFactor = 0.5 } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { BackoffFactor = double.NaN } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { Retry = new PromptRetry { BackoffFactor = double.PositiveInfinity } });
        Assert.Throws<ArgumentException>(() => settings.Limits = settings.Limits with { MaxToolCalls = -1 });

        Assert.Equal(0, changes);
        Assert.Equal("sonnet", settings.Model!.Id);
        Assert.Equal("medium", settings.ModelSettings["effort"]);
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
        settings.Mounts["docs"] = new Mock<IStore>().Object;

        foreach (PromptTool tool in settings.Tools)
        {
            settings.Tools.Add(Tool(tool.Name + "2"));
        }

        foreach (KeyValuePair<string, IStore> mount in settings.Mounts)
        {
            settings.Mounts.Remove(mount.Key);
        }

        Assert.Equal(["alpha", "alpha2"], settings.Tools.Select(tool => tool.Name));
        Assert.Empty(settings.Mounts);
    }

    [Fact]
    public void MountsBehaveLikeADictionaryThatChecksNamesAndTellsTheOwner()
    {
        PromptSessionSettings settings = Settings();
        IStore first = new Mock<IStore>().Object;
        IStore second = new Mock<IStore>().Object;

        settings.Mounts["a"] = first;
        settings.Mounts.Add("b", second);
        settings.Mounts.Add(new KeyValuePair<string, IStore>("c", first));
        settings.Mounts["a"] = second;

        Assert.Equal(["a", "b", "c"], settings.Mounts.Keys.Order());
        Assert.Equal(3, settings.Mounts.Count);
        Assert.Contains(second, settings.Mounts.Values);
        Assert.True(settings.Mounts.ContainsKey("b"));
        Assert.True(settings.Mounts.TryGetValue("a", out IStore? found) && found == second);
        Assert.False(settings.Mounts.TryGetValue("missing", out _));
        Assert.Contains(new KeyValuePair<string, IStore>("a", second), settings.Mounts);
        Assert.False(settings.Mounts.IsReadOnly);
        KeyValuePair<string, IStore>[] copy = new KeyValuePair<string, IStore>[4];
        settings.Mounts.CopyTo(copy, 1);
        Assert.Contains(copy, entry => entry.Key == "b");
        Assert.True(settings.Mounts.Remove("b"));
        Assert.False(settings.Mounts.Remove("b"));
        Assert.True(settings.Mounts.Remove(new KeyValuePair<string, IStore>("c", first)));
        Assert.False(settings.Mounts.Remove(new KeyValuePair<string, IStore>("c", first)));
        settings.Mounts.Clear();
        settings.Mounts.Clear();
        settings.Mounts.Add("a", first);
        Assert.Throws<ArgumentException>(() => settings.Mounts.Add("a", first));
        Assert.Equal(8, changes);
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("my/test")]
    [InlineData("a/b/c")]
    [InlineData("my docs/v1.2")]
    public void AMountCanBeAtAnyRelativePath(string name)
    {
        PromptSessionSettings settings = Settings();
        IStore store = new Mock<IStore>().Object;

        settings.Mounts[name] = store;

        Assert.Same(store, settings.Mounts[name]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("../a")]
    [InlineData("a\\b")]
    public void AMountWithANameThatIsNotAllowedIsRefused(string name)
    {
        PromptSessionSettings settings = Settings();

        Assert.Throws<ArgumentException>(() => settings.Mounts[name] = new Mock<IStore>().Object);
        Assert.Throws<ArgumentException>(() => settings.Mounts.Add(name, new Mock<IStore>().Object));
        Assert.Empty(settings.Mounts);
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
        settings.Model = TestModels.Model;
        settings.Tools.Add(Tool("alpha"));
        settings.Mounts["docs"] = new Mock<IStore>().Object;
        settings.McpServers["db"] = PromptMcpServer.Http("https://example.com");

        PromptSessionSettingsSnapshot snapshot = settings.Snapshot();
        settings.Model = null;
        settings.Tools.Clear();
        settings.Mounts.Clear();
        settings.McpServers.Clear();

        Assert.Equal("test-model", snapshot.Model!.Id);
        Assert.Equal("medium", snapshot.ModelSettings["effort"]);
        Assert.Equal(["alpha"], snapshot.Tools.Select(tool => tool.Name));
        Assert.Equal(["docs"], snapshot.Mounts.Keys);
        Assert.Equal(["db"], snapshot.McpServers.Keys);
    }

    [Fact]
    public void AfterItIsClosedEveryChangeThrowsButReadingStillWorks()
    {
        PromptSessionSettings settings = Settings();
        settings.Tools.Add(Tool("alpha"));
        settings.Close();

        Assert.Throws<ObjectDisposedException>(() => settings.Instructions = "x");
        Assert.Throws<ObjectDisposedException>(() => settings.IsPersistent = true);
        Assert.Throws<ObjectDisposedException>(() => settings.Tools.Add(Tool("beta")));
        Assert.Throws<ObjectDisposedException>(() => settings.Tools.Clear());
        Assert.Throws<ObjectDisposedException>(() => settings.Mounts["a"] = new Mock<IStore>().Object);
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
                settings.Mounts[$"mount-{worker}-{number}"] = new Mock<IStore>().Object;
                _ = settings.Snapshot();
            }
        })));

        Assert.Equal(400, settings.Tools.Count);
        Assert.Equal(400, settings.Mounts.Count);
    }

    [Fact]
    public void TheRetryAndTheWindowLimitAreCopiedToAnotherConfiguration()
    {
        PromptSessionSettings from = Settings();
        PromptSessionSettings to = Settings();
        Func<Exception, bool> when = _ => false;
        PromptWindowLimit limit = new() { Reached = _ => true, Interval = TimeSpan.FromSeconds(5) };
        from.Limits = from.Limits with { Retry = new PromptRetry { MaxAttempts = 5, InitialDelay = TimeSpan.FromMilliseconds(10), BackoffFactor = 1.5, MaxDelay = TimeSpan.FromSeconds(9), ShouldRetry = when } };
        from.Limits = from.Limits with { WindowLimit = limit };

        from.Snapshot().CopyTo(to);

        Assert.Equal((5, TimeSpan.FromMilliseconds(10), 1.5, TimeSpan.FromSeconds(9)), (to.Limits.Retry!.MaxAttempts, to.Limits.Retry.InitialDelay, to.Limits.Retry.BackoffFactor, to.Limits.Retry.MaxDelay));
        Assert.Same(when, to.Limits.Retry.ShouldRetry);
        Assert.Same(limit, to.Limits.WindowLimit);
    }
}
