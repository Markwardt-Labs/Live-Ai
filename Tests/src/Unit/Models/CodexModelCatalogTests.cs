namespace Markwardt.LiveAi.Tests.Unit.Models;

public sealed class CodexModelCatalogTests
{
    private readonly string catalog = """
        {
          "models": [
            { "slug": "gpt-reserve", "display_name": "Reserve", "visibility": "hide", "context_window": 1000, "supported_reasoning_levels": [{ "effort": "low", "description": "x" }] },
            { "slug": "gpt-5.6-terra", "display_name": "Terra", "visibility": "list", "context_window": 272000, "supported_reasoning_levels": [{ "effort": "low", "description": "x" }, { "effort": "high", "description": "y" }] },
            { "slug": "plain", "visibility": "list" }
          ]
        }
        """;

    [Fact]
    public async Task List_GivesTheModelsCodexOffersAndLeavesTheHiddenOnesOut()
    {
        using TempFolder temp = new();
        IReadOnlyList<PromptModel> models = await new CodexModelCatalog(Script(temp, $"cat <<'EOF'\n{catalog}\nEOF")).List();

        Assert.Equal(["gpt-5.6-terra", "plain"], models.Select(model => model.Id));
        Assert.Equal(("Terra", 272000), (models[0].Name, models[0].ContextLimit));
        Assert.Equal(["low", "high"], models[0].Efforts);
        Assert.Equal((null, null, null), (models[1].Name, models[1].ContextLimit, models[1].Efforts));
    }

    [Fact]
    public async Task List_IgnoresAnEffortWithoutAName()
    {
        using TempFolder temp = new();
        string body = """echo '{"models":[{"slug":"m","visibility":"list","supported_reasoning_levels":[{"effort":null},{"effort":"low"}]}]}'""";

        Assert.Equal(["low"], (await new CodexModelCatalog(Script(temp, body)).List())[0].Efforts);
    }

    [Fact]
    public async Task List_AsksForTheCatalogWithTheConfigFolder()
    {
        using TempFolder temp = new();
        string command = Script(temp, $"printf '%s ' \"$@\" > \"{temp.Combine("args.txt")}\"\nprintf '%s' \"$CODEX_HOME\" > \"{temp.Combine("home.txt")}\"\necho '{{\"models\":[]}}'");

        Assert.Empty(await new CodexModelCatalog(command, temp.Combine("state")).List());

        Assert.Equal("debug models ", await File.ReadAllTextAsync(temp.Combine("args.txt")));
        Assert.Equal(temp.Combine("state"), await File.ReadAllTextAsync(temp.Combine("home.txt")));
    }

    [Fact]
    public async Task List_GivesNothingWhenCodexIsNotThere()
        => Assert.Empty(await new CodexModelCatalog("/nonexistent/codex").List());

    [Fact]
    public async Task List_GivesNothingWhenCodexFails()
    {
        using TempFolder temp = new();

        Assert.Empty(await new CodexModelCatalog(Script(temp, "echo broken >&2\nexit 1")).List());
    }

    [Theory]
    [InlineData("echo not json")]
    [InlineData("echo '{\"other\":1}'")]
    [InlineData("echo '{\"models\":[{\"visibility\":\"list\"}]}'")]
    public async Task List_GivesNothingWhenTheAnswerCannotBeRead(string body)
    {
        using TempFolder temp = new();

        Assert.Empty(await new CodexModelCatalog(Script(temp, body)).List());
    }

    [Fact]
    public async Task List_GivesNothingWhenCodexDoesNotAnswerInTime()
    {
        using TempFolder temp = new();

        Assert.Empty(await new CodexModelCatalog(Script(temp, "sleep 30")) { Timeout = TimeSpan.FromMilliseconds(300) }.List());
    }

    [Fact]
    public async Task List_StopsWhenTheCallerCancels()
    {
        using TempFolder temp = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexModelCatalog(Script(temp, "sleep 30")).List(cancellation.Token));
    }

    private string Script(TempFolder temp, string body)
        => temp.Script("codex", body);
}
