namespace Markwardt.LiveAi.Tests.Integration;

public sealed class PromptSessionStoreTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private PromptSessionLink Link(string? aiSessionId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Provider = "Codex",
            WorkingFolder = "/work",
            AiSessionId = aiSessionId
        };

    [Fact]
    public async Task Find_ReturnsWhatWasSaved()
    {
        PromptSessionStore store = new(temp.Path);
        PromptSessionLink link = Link("abc") with { ForkedFrom = "origin" };

        await store.Save(link, CancellationToken.None);

        Assert.Equal(link, await new PromptSessionStore(temp.Path).Find(link.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TheWorkspaceAndItsSandboxAreStoredWithTheLink()
    {
        PromptSessionStore store = new(temp.Path);
        PromptSessionLink link = Link("abc") with { WorkspacePath = "/project", Sandbox = false };

        await store.Save(link, CancellationToken.None);

        PromptSessionLink found = (await new PromptSessionStore(temp.Path).Find(link.Id, CancellationToken.None))!;
        Assert.Equal(("/project", false), (found.WorkspacePath, found.Sandbox));
    }

    [Fact]
    public async Task OnlyTheLinkIsWrittenAndNothingAboutTheSettings()
    {
        PromptSessionLink link = Link("abc");

        await new PromptSessionStore(temp.Path).Save(link, CancellationToken.None);

        JsonElement written = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(temp.Path, "Sessions")).Single())).RootElement;
        Assert.Equal(["id", "created", "provider", "workingFolder", "aiSessionId", "workspacePath", "sandbox", "forkedFrom", "forkedAt"], written.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Codex", written.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Find_OfAFileWrittenWithMoreThanTheLink_ReadsTheLinkAndIgnoresTheRest()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("Sessions"));
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id:N}.json"), $$$"""{"id":"{{{id:N}}}","provider":"Claude Code","capability":"Max","reasoning":"High","isPersistent":true,"enableShell":true,"mounts":{"a":{"path":"/x"}},"label":"old","workingFolder":"/work","aiSessionId":"abc"}""");

        PromptSessionLink? found = await new PromptSessionStore(temp.Path).Find(id, CancellationToken.None);

        Assert.Equal(new PromptSessionLink { Id = id, Provider = "Claude Code", WorkingFolder = "/work", AiSessionId = "abc" }, found);
    }

    [Fact]
    public async Task List_ReturnsTheReadableLinksNewestFirstAndLeavesOutWhatCannotBeRead()
    {
        PromptSessionStore store = new(temp.Path);
        DateTimeOffset now = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        PromptSessionLink old = Link() with { Created = now.AddDays(-2) };
        PromptSessionLink recent = Link() with { Created = now };
        PromptSessionLink undated = Link();
        await store.Save(old, CancellationToken.None);
        await store.Save(undated, CancellationToken.None);
        await store.Save(recent, CancellationToken.None);
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{Guid.NewGuid():N}.json"), "{ not json");

        IReadOnlyList<PromptSessionLink> links = await store.List(CancellationToken.None);

        Assert.Equal([recent.Id, old.Id, undated.Id], links.Select(link => link.Id));
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
    public void ALinkBecomesASessionWithTheDefaultSettingsAndABecomesBackTheSameLink()
    {
        PromptSessionLink link = Link("abc") with { ForkedFrom = "origin" };

        PromptSessionRecord record = link.ToRecord(new CodexEngine());

        Assert.Equal((PromptCapability.Balanced, PromptReasoning.Medium, null, 0), (record.Capability, record.Reasoning, record.Instructions, record.McpServers.Count));
        Assert.Equal(link, record.ToLink());
    }
}
