namespace Markwardt.LiveAi.Tests.Integration;

public sealed class PromptSessionSettingsStoredTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private Prompter Subject()
        => new(temp.Path) { Environment = new PromptEnvironment { ClaudeCommand = "/no/such/claude", ClaudeConfigFolder = temp.Combine("claude") } };

    private async Task<JsonElement> Stored(string id, Func<JsonElement, bool> condition)
    {
        JsonElement found = default;

        for (int attempt = 0; attempt < 200; attempt++)
        {
            found = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine($"Sessions/{id}.json"))).RootElement.Clone();

            if (condition(found))
            {
                return found;
            }

            await Task.Delay(25);
        }

        return found;
    }

    [Fact]
    public async Task ASettingIsStoredAtOnceWithoutAPromptOrTheSessionClosing()
    {
        string folder = Directory.CreateDirectory(temp.Combine("app")).FullName;
        await using IPromptSession session = await Subject().Create(new ClaudeSetup { WorkspacePath = folder });

        session.Configuration.IsShellAllowed = true;
        session.Configuration.Effort = PromptEffort.Low;
        session.Configuration.Instructions = "Be brief.";

        JsonElement stored = await Stored(session.Id, link => link.GetProperty("isShellAllowed").GetBoolean() && link.GetProperty("effort").GetString() == "Low" && link.GetProperty("instructions").GetString() == "Be brief.");
        Assert.True(stored.GetProperty("isShellAllowed").GetBoolean());
        Assert.Equal(("Low", "Be brief."), (stored.GetProperty("effort").GetString(), stored.GetProperty("instructions").GetString()));
    }

    [Fact]
    public async Task ManyQuickChangesEndWithTheLastValueStored()
    {
        string folder = Directory.CreateDirectory(temp.Combine("app")).FullName;
        await using IPromptSession session = await Subject().Create(new ClaudeSetup { WorkspacePath = folder });

        for (int round = 0; round < 40; round++)
        {
            session.Configuration.IsWebAllowed = round % 2 == 0;
            session.Configuration.IsImageGenerationAllowed = round % 3 == 0;
            session.Configuration.Instructions = $"round {round}";
        }

        JsonElement stored = await Stored(session.Id, link => link.GetProperty("instructions").GetString() == "round 39");
        Assert.Equal(("round 39", false, true), (stored.GetProperty("instructions").GetString(), stored.GetProperty("isWebAllowed").GetBoolean(), stored.GetProperty("isImageGenerationAllowed").GetBoolean()));
    }

    [Fact]
    public async Task AChangeIsStillStoredWhenTheSessionIsClosedRightAfterwards()
    {
        string folder = Directory.CreateDirectory(temp.Combine("app")).FullName;
        Prompter prompter = Subject();
        IPromptSession session = await prompter.Create(new ClaudeSetup { WorkspacePath = folder });

        session.Configuration.IsShellAllowed = true;
        await session.DisposeAsync();

        await using IPromptSession loaded = await prompter.Load(session.Id);
        Assert.True(loaded.Configuration.IsShellAllowed);
    }
}
