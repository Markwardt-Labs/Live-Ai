namespace Markwardt.LiveAi.Tests.Unit.Models;

[Collection("Processes")]
public sealed class CodexEngineModelsTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly string catalog = """
        {
          "models": [
            { "slug": "gpt-reserve", "display_name": "Reserve", "visibility": "hide", "supported_reasoning_levels": [{ "effort": "low", "description": "x" }] },
            { "slug": "gpt-5.6-terra", "display_name": "Terra", "description": "Older balanced model.", "visibility": "list", "default_reasoning_level": "high", "input_modalities": ["text", "image"], "supported_reasoning_levels": [{ "effort": "low", "description": "x" }, { "effort": "high", "description": "y" }] },
            { "slug": "plain", "visibility": "list", "input_modalities": ["text"] }
          ]
        }
        """;

    public void Dispose()
        => temp.Dispose();

    private CodexEngine Engine(string body, string? state = null, TimeSpan? timeout = null)
        => new(temp.Script("codex", body), state) { ModelsTimeout = timeout ?? TimeSpan.FromSeconds(30) };

    private string Prints(string json)
        => $"cat <<'EOF'\n{json}\nEOF";

    [Fact]
    public async Task GetModels_GivesTheModelsCodexOffersAndLeavesTheHiddenOnesOut()
    {
        IReadOnlyList<PromptModel> models = await Engine(Prints(catalog)).GetModels();

        Assert.Equal(["gpt-5.6-terra", "plain"], models.Select(model => model.Id));
        Assert.Equal(("Terra", "Older balanced model.", true), (models[0].Name, models[0].Description, models[0].AcceptsImages));
        PromptModelSetting effort = Assert.Single(models[0].Settings!);
        Assert.Equal(["low", "high"], effort.Choices);
        Assert.Equal("high", effort.Default);
        Assert.Equal((null, false), (models[1].Name, models[1].AcceptsImages));
        Assert.Empty(models[1].Settings!);
    }

    [Fact]
    public async Task GetModels_IgnoresADefaultLevelThatIsNotOneOfTheModelsLevels()
    {
        string json = """{"models":[{"slug":"m","visibility":"list","default_reasoning_level":"ultra","supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"}]}]}""";

        Assert.Equal("medium", (await Engine(Prints(json)).GetModels()).Single().Settings!.Single().Default);
    }

    [Fact]
    public async Task GetModels_AsksForTheCatalogWithTheKeyAndTheConfigFolder()
    {
        string body = $"printf '%s ' \"$@\" > \"{temp.Combine("args.txt")}\"\nprintf '%s' \"$CODEX_API_KEY|$CODEX_HOME\" > \"{temp.Combine("env.txt")}\"\necho '{{\"models\":[]}}'";

        Assert.Empty(await Engine(body, temp.Combine("state")).GetModels("key"));

        Assert.Equal("debug models ", await File.ReadAllTextAsync(temp.Combine("args.txt")));
        Assert.Equal($"key|{temp.Combine("state")}", await File.ReadAllTextAsync(temp.Combine("env.txt")));
    }

    [Fact]
    public async Task GetModels_GivesNoneWhenCodexIsNotThereOrFails()
    {
        Assert.Empty(await new CodexEngine(temp.Combine("not-installed")).GetModels());
        Assert.Empty(await Engine("echo broken >&2\nexit 1").GetModels());
    }

    [Theory]
    [InlineData("echo not json")]
    [InlineData("echo '{\"other\":1}'")]
    [InlineData("echo '{\"models\":[{\"visibility\":\"list\"}]}'")]
    public async Task GetModels_GivesNoneWhenTheAnswerCannotBeRead(string body)
        => Assert.Empty(await Engine(body).GetModels());

    [Fact]
    public async Task GetModels_GivesNoneWhenCodexDoesNotAnswerInTime()
        => Assert.Empty(await Engine("sleep 120", timeout: TimeSpan.FromMilliseconds(300)).GetModels());

    [Fact]
    public async Task GetModels_StopsWhenTheCallerCancels()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine("sleep 120").GetModels(cancellation: cancellation.Token));
    }

    [Fact]
    public async Task GetModels_IgnoresALevelWithoutAName()
    {
        string json = """{"models":[{"slug":"m","visibility":"list","supported_reasoning_levels":[{"effort":null},{"effort":"low"}]}]}""";

        Assert.Equal(["low"], (await Engine(Prints(json)).GetModels()).Single().Settings!.Single().Choices);
    }

    [Fact]
    public async Task CreateTestStart_RunsTheModelDescribedAsFastAtItsLowestEffort()
    {
        string json = """{"models":[{"slug":"big","visibility":"list","description":"Frontier.","supported_reasoning_levels":[{"effort":"low"},{"effort":"high"}]},{"slug":"quick","visibility":"list","description":"Older fast and efficient model.","supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"}]}]}""";

        ProcessStartInfo start = await Engine(Prints(json)).CreateTestStart("key");

        Assert.Equal("quick", start.ArgumentList[start.ArgumentList.IndexOf("-m") + 1]);
        Assert.Contains("model_reasoning_effort=\"low\"", start.ArgumentList);
        Assert.Equal("key", start.Environment["CODEX_API_KEY"]);
        Assert.Equal("-", start.ArgumentList[^1]);
    }

    [Fact]
    public async Task CreateTestStart_FallsBackToTheFirstModelAndThenToNoneAtAll()
    {
        string json = """{"models":[{"slug":"first","visibility":"list"},{"slug":"second","visibility":"list"}]}""";

        ProcessStartInfo first = await Engine(Prints(json)).CreateTestStart(null);
        ProcessStartInfo none = await new CodexEngine(temp.Combine("not-installed")).CreateTestStart(null);

        Assert.Equal("first", first.ArgumentList[first.ArgumentList.IndexOf("-m") + 1]);
        Assert.DoesNotContain("-m", none.ArgumentList);
        Assert.DoesNotContain(none.ArgumentList, argument => argument.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
    }
}
