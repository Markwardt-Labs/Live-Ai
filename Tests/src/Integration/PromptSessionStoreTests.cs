namespace Markwardt.LiveAi.Tests.Integration;

public sealed class PromptSessionStoreTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private PromptSessionLink Link(string? aiSessionId = null)
        => new()
        {
            LastLoaded = TestModels.Loaded,
            Id = Guid.NewGuid(),
            Provider = PromptProvider.Codex,
            WorkingFolder = "/work",
            AiSessionId = aiSessionId
        };

    [Fact]
    public async Task Find_ReturnsWhatWasSaved()
    {
        PromptSessionStore store = new(temp.Path);
        PromptSessionLink link = Link("abc") with { ForkedFrom = "origin" };

        await store.Save(link, CancellationToken.None);

        Assert.Equal(link with { Version = 5 }, await new PromptSessionStore(temp.Path).Find(link.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TheWorkspaceAndItsSystemAccessAreStoredWithTheLink()
    {
        PromptSessionStore store = new(temp.Path);
        PromptSessionLink link = Link("abc") with { WorkspacePath = "/project", IsSystemAccessAllowed = true };

        await store.Save(link, CancellationToken.None);

        PromptSessionLink found = (await new PromptSessionStore(temp.Path).Find(link.Id, CancellationToken.None))!;
        Assert.Equal(("/project", true), (found.WorkspacePath, found.IsSystemAccessAllowed));
    }

    [Fact]
    public async Task TheLinkAndTheConfigurationOfTheSessionAreWritten()
    {
        PromptSessionLink link = Link("abc");

        await new PromptSessionStore(temp.Path).Save(link, CancellationToken.None);

        JsonElement written = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(temp.Path, "Sessions")).Single())).RootElement;
        Assert.Equal(["version", "id", "lastLoaded", "provider", "workingFolder", "aiSessionId", "workspacePath", "isSystemAccessAllowed", "forkedFrom", "forkedAt", "options", "isShellAllowed", "isWebAllowed", "instructions", "isImageGenerationAllowed", "capability", "effort"], written.EnumerateObject().Select(property => property.Name));
        Assert.Equal("codex", written.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task ASavedLink_CanBeReadByTheCurrentUserOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await new PromptSessionStore(temp.Path).Save(Link("abc") with { Setup = new ClaudeSetup { ApiKey = "key" } }, CancellationToken.None);

        string file = Directory.GetFiles(Path.Combine(temp.Path, "Sessions")).Single();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Contains("\"apiKey\": \"key\"", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Find_OfAFileWrittenWithMoreThanTheLink_ReadsTheLinkAndIgnoresTheRest()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json"), $$$"""{"version":5,"id":"{{{id:N}}}","lastLoaded":"2026-11-01T12:00:00+00:00","provider":"claude","capability":"Max","reasoning":"High","isPersistent":true,"isShellAllowed":true,"mounts":{"a":{"path":"/x"}},"label":"old","workingFolder":"/work","aiSessionId":"abc","options":{"tool":"claude"}}""");

        PromptSessionLink? found = await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None);

        Assert.Equal(new PromptSessionLink { LastLoaded = TestModels.Loaded, Id = id, Provider = PromptProvider.Claude, WorkingFolder = "/work", AiSessionId = "abc", Capability = PromptCapability.Max, IsShellAllowed = true, Setup = new ClaudeSetup(), Version = 5 }, found);
    }

    [Fact]
    public async Task List_ReturnsTheReadableLinksNewestFirstAndLeavesOutWhatCannotBeRead()
    {
        PromptSessionStore store = new(temp.Path);
        DateTimeOffset now = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        PromptSessionLink old = Link() with { LastLoaded = now.AddDays(-2) };
        PromptSessionLink recent = Link() with { LastLoaded = now };
        PromptSessionLink middle = Link() with { LastLoaded = now.AddDays(-1) };
        await store.Save(old, CancellationToken.None);
        await store.Save(middle, CancellationToken.None);
        await store.Save(recent, CancellationToken.None);
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{Guid.NewGuid():N}.json"), "{ not json");

        IReadOnlyList<PromptSessionLink> links = await store.List(CancellationToken.None);

        Assert.Equal([recent.Id, middle.Id, old.Id], links.Select(link => link.Id));
    }

    [Fact]
    public async Task List_OfAFolderThatDoesNotExist_ReturnsNothing()
        => Assert.Empty(await new PromptSessionStore(temp.Combine("none")).List(CancellationToken.None));

    [Fact]
    public async Task Save_ReplacesTheLinkOfTheSameIdentifier()
    {
        PromptSessionStore store = new(temp.Path);
        PromptSessionLink link = Link();

        await store.Save(link, CancellationToken.None);
        await store.Save(link with { AiSessionId = "abc" }, CancellationToken.None);

        Assert.Equal("abc", (await store.Find(link.Id, CancellationToken.None))?.AiSessionId);
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "Sessions")));
    }

    [Fact]
    public async Task Save_WritesTheIdentifierWithoutHyphens()
    {
        PromptSessionLink link = Link();

        await new PromptSessionStore(temp.Path).Save(link, CancellationToken.None);

        string text = await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(temp.Path, "Sessions")).Single());
        Assert.Contains($"\"id\": \"{link.Id:N}\"", text);
    }

    [Fact]
    public async Task Delete_RemovesTheLinkAndToleratesOneThatIsGone()
    {
        PromptSessionStore store = new(temp.Path);
        PromptSessionLink link = Link();
        await store.Save(link, CancellationToken.None);

        await store.Delete(link.Id);
        await store.Delete(link.Id);

        Assert.Null(await store.Find(link.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Find_WhenNothingIsStored_ReturnsNull()
        => Assert.Null(await new PromptSessionStore(temp.Path).Find(Guid.NewGuid(), CancellationToken.None));

    [Fact]
    public async Task Find_WhenTheFileIsUnreadable_Throws()
    {
        PromptSessionStore store = new(temp.Path);
        PromptSessionLink link = Link();
        await store.Save(link, CancellationToken.None);
        await File.WriteAllTextAsync(Directory.GetFiles(Path.Combine(temp.Path, "Sessions")).Single(), "{ not json");

        await Assert.ThrowsAsync<PromptSessionException>(() => store.Find(link.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Save_WithoutAFolder_UsesTheLocalApplicationDataFolder()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Markwardt.LiveAi", "Sessions");
        PromptSessionLink link = Link();
        string file = Path.Combine(expected, $"{link.Id:N}.json");

        try
        {
            await new PromptSessionStore(null).Save(link, CancellationToken.None);

            Assert.True(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ALinkBecomesASessionWithNothingSelectedAndBecomesBackTheSameLink()
    {
        PromptSessionLink link = Link("abc") with { ForkedFrom = "origin" };

        PromptSessionRecord record = link.ToRecord(new CodexEngine());

        Assert.Equal((null, 0, null, 0), (record.Model, record.ModelSettings.Count, record.Instructions, record.McpServers.Count));
        Assert.Equal(link, record.ToLink());
    }

    private string Legacy(Guid id, string provider = "Claude Code", string sandbox = "false")
        => $$"""{ "id": "{{id:N}}", "created": "2026-11-03T17:25:02+00:00", "provider": "{{provider}}", "workingFolder": "/work", "aiSessionId": "tool-session", "workspacePath": "/work", "sandbox": {{sandbox}}, "forkedFrom": null, "forkedAt": null, "model": "opus", "modelSettings": { "effort": "xhigh" } }""";

    [Fact]
    public async Task Find_UpgradesALinkOfTheFirstLayoutAndKeepsTheOriginalNextToIt()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        string path = temp.Combine($"Sessions/{id:N}.json");
        await File.WriteAllTextAsync(path, Legacy(id));

        PromptSessionLink found = (await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None))!;

        Assert.Equal(("tool-session", true, PromptCapability.Strong, PromptEffort.Max, 5), (found.AiSessionId, found.IsSystemAccessAllowed, found.Capability, found.Effort, found.Version));
        Assert.IsType<ClaudeSetup>(found.Setup);
        Assert.Equal(Legacy(id), await File.ReadAllTextAsync(path + ".v1.bak"));
        JsonObject stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal((5, "claude"), (stored["version"]!.GetValue<int>(), stored["provider"]!.GetValue<string>()));
        Assert.False(stored.ContainsKey("upgradedFrom"));
        Assert.False(stored.ContainsKey("sandbox"));
        Assert.Equal(["Sessions"], Directory.GetDirectories(temp.Path).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(temp.Combine("Sessions"), "*.tmp"));
    }

    [Fact]
    public async Task AnUpgradedLink_IsReadAgainAsTheCurrentLayoutWithoutAnotherBackupAndCanBeWritten()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json"), Legacy(id));
        PromptSessionStore store = new(temp.Path);
        await store.Find(id, CancellationToken.None);

        PromptSessionLink again = (await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None))!;
        Assert.Equal(5, again.Version);
        Assert.DoesNotContain(Directory.GetFiles(temp.Combine("Sessions"), "*.bak"), file => !file.EndsWith(".v1.bak", StringComparison.Ordinal));

        await store.Save(again with { IsShellAllowed = true }, CancellationToken.None);

        PromptSessionLink saved = (await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None))!;
        Assert.Equal((5, true), (saved.Version, saved.IsShellAllowed));
        Assert.DoesNotContain("upgradedFrom", await File.ReadAllTextAsync(temp.Combine($"Sessions/{id:N}.json")));
    }

    [Fact]
    public async Task List_UpgradesTheLinksOfTheFirstLayoutAndLeavesOutOnesOfAnUnknownToolOrANewerVersion()
    {
        Guid good = Guid.NewGuid();
        Guid strange = Guid.NewGuid();
        Guid newer = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{good:N}.json"), Legacy(good, "Codex"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{strange:N}.json"), Legacy(strange, "Some Other Tool"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{newer:N}.json"), $$"""{ "version": 9, "id": "{{newer:N}}", "provider": "codex", "workingFolder": "/w" }""");

        IReadOnlyList<PromptSessionLink> links = await new PromptSessionStore(temp.Path).List(CancellationToken.None);

        PromptSessionLink only = Assert.Single(links);
        Assert.Equal((good, PromptProvider.Codex), (only.Id, only.Provider));
        Assert.IsType<CodexSetup>(only.Setup);
        Assert.False(File.Exists(temp.Combine($"Sessions/{strange:N}.json.v1.bak")));
    }

    [Fact]
    public async Task Find_RefusesALinkANewerVersionWroteAndLeavesItAlone()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        string text = $$"""{ "version": 9, "id": "{{id:N}}", "provider": "codex", "workingFolder": "/w" }""";
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json"), text);

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => new PromptSessionStore(temp.Path).Find(id, CancellationToken.None));

        Assert.Contains("newer version", exception.Message);
        Assert.Equal(text, await File.ReadAllTextAsync(temp.Combine($"Sessions/{id:N}.json")));
    }

    [Fact]
    public async Task Find_DoesNotOverwriteABackupThatIsAlreadyThere()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json"), Legacy(id));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json.v1.bak"), "first backup");

        await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None);

        Assert.Equal("first backup", await File.ReadAllTextAsync(temp.Combine($"Sessions/{id:N}.json.v1.bak")));
    }

    [Fact]
    public async Task Delete_RemovesTheBackupsOfAnUpgradeToo()
    {
        Guid id = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json"), Legacy(id));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{other:N}.json"), Legacy(other));
        PromptSessionStore store = new(temp.Path);
        await store.Find(id, CancellationToken.None);
        await store.Find(other, CancellationToken.None);

        await store.Delete(id);

        Assert.Equal([$"{other:N}.json", $"{other:N}.json.v1.bak"], Directory.GetFiles(temp.Combine("Sessions")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task TheBackupOfAnUpgradedLink_CanBeReadByTheCurrentUserOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        string path = temp.Combine($"Sessions/{id:N}.json");
        await File.WriteAllTextAsync(path, Legacy(id));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".v1.bak"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Theory]
    [InlineData(PromptProvider.Claude, "claude")]
    [InlineData(PromptProvider.Codex, "codex")]
    [InlineData(PromptProvider.Local, "local")]
    public async Task TheProviderIsStoredUnderTheNameOfTheEnumInLowerCase(PromptProvider provider, string expected)
    {
        PromptSessionLink link = Link("abc") with { Provider = provider };

        await new PromptSessionStore(temp.Path).Save(link, CancellationToken.None);

        JsonElement written = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(temp.Path, "Sessions")).Single())).RootElement;
        Assert.Equal(expected, written.GetProperty("provider").GetString());
        Assert.Equal(provider, (await new PromptSessionStore(temp.Path).Find(link.Id, CancellationToken.None))!.Provider);
    }

    [Fact]
    public async Task Find_UpgradesAFileOfTheSecondLayoutWhoseProviderIsADisplayName()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        string path = temp.Combine($"Sessions/{id:N}.json");
        string text = $$"""{ "id": "{{id:N}}", "provider": "OpenCode", "workingFolder": "/work", "options": { "tool": "local" }, "capability": "Strong" }""";
        await File.WriteAllTextAsync(path, text);

        PromptSessionLink found = (await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None))!;

        Assert.Equal((PromptProvider.Local, PromptCapability.Strong, 5), (found.Provider, found.Capability, found.Version));
        Assert.Equal(text, await File.ReadAllTextAsync(path + ".v2.bak"));
        Assert.Equal("local", JsonNode.Parse(await File.ReadAllTextAsync(path))!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Find_OfAFileOfAToolItDoesNotKnow_IsUnreadable()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json"), Legacy(id, "Some Other Tool"));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => new PromptSessionStore(temp.Path).Find(id, CancellationToken.None));

        Assert.Contains("unreadable", exception.Message);
    }

    [Fact]
    public async Task Find_GivesAFileThatNeverSaidWhenItWasLoadedTheTimeItWasLastWritten()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        string path = temp.Combine($"Sessions/{id:N}.json");
        await File.WriteAllTextAsync(path, $$"""{ "version": 5, "id": "{{id:N}}", "provider": "claude", "workingFolder": "/w" }""");
        DateTime written = new(2026, 3, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        PromptSessionLink found = (await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None))!;

        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), found.LastLoaded);
    }

    [Fact]
    public async Task Find_UpgradesAFileThatNeverSaidWhenItWasCreatedAndStoresTheTimeItWasLastWritten()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        string path = temp.Combine($"Sessions/{id:N}.json");
        await File.WriteAllTextAsync(path, Legacy(id).Replace("\"created\": \"2026-11-03T17:25:02+00:00\"", "\"created\": null"));
        DateTime written = new(2026, 3, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        PromptSessionLink found = (await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None))!;

        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), found.LastLoaded);
        JsonObject stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.False(stored.ContainsKey("created"));
        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), stored["lastLoaded"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Find_UpgradesAFileOfTheFourthLayoutWhoseSandboxSwitchIsOffToTheSystemAccessSwitchBeingOn()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        string path = temp.Combine($"Sessions/{id:N}.json");
        string text = $$"""{ "version": 4, "id": "{{id:N}}", "lastLoaded": "2026-11-01T12:00:00+00:00", "provider": "claude", "workingFolder": "/work", "isSandboxed": false, "options": { "tool": "claude" } }""";
        await File.WriteAllTextAsync(path, text);

        PromptSessionLink found = (await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None))!;

        Assert.Equal((true, 5), (found.IsSystemAccessAllowed, found.Version));
        Assert.Equal(text, await File.ReadAllTextAsync(path + ".v4.bak"));
        JsonObject stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal((true, false), (stored["isSystemAccessAllowed"]!.GetValue<bool>(), stored.ContainsKey("isSandboxed")));
    }
}
