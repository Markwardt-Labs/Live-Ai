namespace Markwardt.LiveAi.Tests.Integration;

public sealed class PromptSessionUpgradeTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly string id = Guid.NewGuid().ToString("N");

    public void Dispose()
        => temp.Dispose();

    private async Task Store()
    {
        Directory.CreateDirectory(temp.Combine("Sessions"));
        Directory.CreateDirectory(temp.Combine("app"));
        Directory.CreateDirectory(temp.Combine("claude/projects/app"));
        await File.WriteAllTextAsync(temp.Combine("claude/projects/app/tool-session.jsonl"), string.Empty);
        string folder = temp.Combine("app");
        await File.WriteAllTextAsync(temp.Combine($"Sessions/{id}.json"), $$"""{ "id": "{{id}}", "provider": "Claude Code", "workingFolder": "{{folder}}", "aiSessionId": "tool-session", "workspacePath": "{{folder}}", "sandbox": false, "model": "opus", "modelSettings": { "effort": "high" } }""");
    }

    private Prompter Subject()
        => new(temp.Path) { Environment = new PromptEnvironment { ClaudeCommand = "/no/such/claude", ClaudeConfigFolder = temp.Combine("claude") } };

    [Fact]
    public async Task ASessionOfTheFirstLayout_IsListedAndLoadsWithWhatItStored()
    {
        await Store();
        Prompter prompter = Subject();

        PromptSessionInfo info = Assert.Single(await prompter.List());
        await using IPromptSession session = await prompter.Load(id);

        Assert.Equal((id, PromptProvider.Claude), (info.Id, info.Provider));
        Assert.Equal((PromptCapability.Strong, PromptEffort.High, true), (session.Configuration.Capability, session.Configuration.Effort, session.Configuration.IsSystemAccessAllowed));
        Assert.Equal(temp.Combine("app"), session.WorkspacePath);
    }

    [Fact]
    public async Task TheSettingsAnUpgradedSessionIsGivenAreStoredWhenItIsDisposed()
    {
        await Store();
        Prompter prompter = Subject();

        await using (IPromptSession session = await prompter.Load(id))
        {
            session.Configuration.IsShellAllowed = true;
            session.Configuration.Instructions = "work in the foreground";
        }

        await using IPromptSession again = await Subject().Load(id);
        Assert.Equal((true, "work in the foreground"), (again.Configuration.IsShellAllowed, again.Configuration.Instructions));
    }

    [Fact]
    public async Task ASessionTheCurrentVersionCreated_IsStoredAtTheCurrentLayoutWithoutABackup()
    {
        Prompter prompter = Subject();
        Directory.CreateDirectory(temp.Combine("app"));
        await (await prompter.Create(new ClaudeSetup { WorkspacePath = temp.Combine("app") })).DisposeAsync();

        Assert.Empty(Directory.GetFiles(temp.Combine("Sessions"), "*.bak"));
        Assert.Equal(5, JsonNode.Parse(await File.ReadAllTextAsync(Directory.GetFiles(temp.Combine("Sessions"), "*.json").Single()))!["version"]!.GetValue<int>());
    }
}
