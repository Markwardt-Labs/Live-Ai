namespace Markwardt.LiveAi.Tests.Integration;

public sealed class PromptSessionMoveTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private string Encoded(string folder)
        => System.Text.RegularExpressions.Regex.Replace(Path.GetFullPath(folder), "[^a-zA-Z0-9]", "-");

    private Prompter Subject()
        => new(temp.Path) { Environment = new PromptEnvironment { ClaudeCommand = "/no/such/claude", ClaudeConfigFolder = temp.Combine("claude") } };

    private async Task<string> Started(Prompter prompter, string folder)
    {
        await using IPromptSession created = await prompter.Create(new ClaudeSetup { WorkspacePath = folder });
        string id = created.Id;
        string link = temp.Combine($"Sessions/{id}.json");
        JsonObject content = JsonNode.Parse(await File.ReadAllTextAsync(link))!.AsObject();
        content["aiSessionId"] = "tool-session";
        await File.WriteAllTextAsync(link, content.ToJsonString());
        string projects = temp.Combine($"claude/projects/{Encoded(folder)}");
        Directory.CreateDirectory(projects);
        await File.WriteAllTextAsync(Path.Combine(projects, "tool-session.jsonl"), string.Empty);
        return id;
    }

    [Fact]
    public async Task Move_OfASessionWhoseFolderIsAlreadyGone_BringsTheConversationToTheNewFolderAndLoadsThere()
    {
        Prompter prompter = Subject();
        string old = temp.Combine("old");
        string moved = temp.Combine("moved");
        Directory.CreateDirectory(old);
        string id = await Started(prompter, old);
        Directory.Move(old, moved);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => prompter.Load(id));
        await prompter.Move(id, moved);

        Assert.True(File.Exists(temp.Combine($"claude/projects/{Encoded(moved)}/tool-session.jsonl")));
        Assert.Equal(moved, Assert.Single(await prompter.List()).WorkspacePath);
        await using IPromptSession loaded = await prompter.Load(id);
        Assert.Equal(moved, loaded.WorkspacePath);
    }

    [Fact]
    public async Task Move_ToAFolderThatIsNotThere_LeavesTheSessionWhereItWas()
    {
        Prompter prompter = Subject();
        string folder = temp.Combine("app");
        Directory.CreateDirectory(folder);
        string id = await Started(prompter, folder);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => prompter.Move(id, temp.Combine("missing")));

        Assert.Equal(folder, Assert.Single(await prompter.List()).WorkspacePath);
        Assert.False(Directory.Exists(temp.Combine($"claude/projects/{Encoded(temp.Combine("missing"))}")));
    }

    [Fact]
    public async Task Move_KeepsTheSettingsAndTheSystemAccess()
    {
        Prompter prompter = Subject();
        string old = temp.Combine("old");
        string moved = temp.Combine("moved");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(moved);
        string id;
        prompter.Environment = prompter.Environment with { IsSystemAccessAllowed = true, IsShellAllowed = true, Instructions = "keep" };
        await using (IPromptSession created = await prompter.Create(new ClaudeSetup { WorkspacePath = old }))
        {
            id = created.Id;
        }

        await prompter.Move(id, moved);

        await using IPromptSession loaded = await prompter.Load(id);
        Assert.Equal((true, true, "keep", moved), (loaded.Configuration.IsSystemAccessAllowed, loaded.Configuration.IsShellAllowed, loaded.Configuration.Instructions, loaded.WorkspacePath));
    }
}
