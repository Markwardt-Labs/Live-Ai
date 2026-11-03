namespace Markwardt.LiveAi.Tests.Unit.Models;

[Collection("Processes")]
public sealed class ClaudeEngineModelsTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly string models = """
        {"type":"control_response","response":{"subtype":"success","request_id":"liveai-models","response":{"models":[
          {"value":"default","displayName":"Default (recommended)","description":"Opus 5.5","supportsEffort":true,"supportedEffortLevels":["low","medium","high"]},
          {"value":"opus","displayName":"Opus 5.5","description":"For complex work","supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"]},
          {"value":"claude-haiku-4-5-20251001","displayName":"Haiku 4.5","description":"Fastest","supportsThinkingOff":true},
          {"value":"","displayName":"Nameless"}
        ]}}}
        """.ReplaceLineEndings(string.Empty);

    public void Dispose()
        => temp.Dispose();

    private ClaudeEngine Engine(string body, TimeSpan? timeout = null)
        => new(temp.Script("claude", body), temp.Combine("claude-state")) { ModelsTimeout = timeout ?? TimeSpan.FromSeconds(30) };

    private string Answer(string json)
        => $"IFS= read -r line\nprintf '%s\\n' '{json}'\ncat > /dev/null";

    [Fact]
    public async Task GetModels_GivesWhatTheToolReportsToAClientWithTheEffortsEachModelAccepts()
    {
        IReadOnlyList<PromptModel> listed = await Engine(Answer(models)).GetModels();

        Assert.Equal(["opus", "claude-haiku-4-5-20251001"], listed.Select(model => model.Id));
        Assert.Equal(("Opus 5.5", "For complex work"), (listed[0].Name, listed[0].Description));
        PromptModelSetting effort = Assert.Single(listed[0].Settings!);
        Assert.Equal(("effort", PromptModelSettingKind.Choice, "medium"), (effort.Id, effort.Kind, effort.Default));
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], effort.Choices);
        Assert.Empty(listed[1].Settings!);
    }

    [Fact]
    public async Task GetModels_LeavesOutTheDefaultEntryBecauseItIsANameForOneOfTheOthers()
        => Assert.DoesNotContain(await Engine(Answer(models)).GetModels(), model => model.Id == "default");

    [Fact]
    public async Task GetModels_AsksWithTheInitializeRequestAndTheKeyAndTheConfigFolder()
    {
        string body = $"IFS= read -r line\nprintf '%s' \"$line\" > \"{temp.Combine("request.txt")}\"\nprintf '%s' \"$ANTHROPIC_API_KEY|$CLAUDE_CONFIG_DIR\" > \"{temp.Combine("env.txt")}\"\nprintf '%s\\n' '{models}'\ncat > /dev/null";

        await Engine(body).GetModels("key");

        JsonElement request = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("request.txt"))).RootElement;
        Assert.Equal(("control_request", "liveai-models", "initialize"), (request.GetProperty("type").GetString(), request.GetProperty("request_id").GetString(), request.GetProperty("request").GetProperty("subtype").GetString()));
        Assert.Equal($"key|{temp.Combine("claude-state")}", await File.ReadAllTextAsync(temp.Combine("env.txt")));
    }

    [Fact]
    public async Task GetModels_IgnoresLinesThatAreNotItsResponseAndStopsTheToolOnceItHasIt()
    {
        string body = $"IFS= read -r line\necho $$ > \"{temp.Combine("pid.txt")}\"\necho 'not json'\necho '{{\"type\":\"system\",\"subtype\":\"init\"}}'\necho '{{\"type\":\"control_response\",\"response\":{{\"request_id\":\"other\",\"response\":{{\"models\":[]}}}}}}'\nprintf '%s\\n' '{models}'\nsleep 120";

        Assert.Equal(2, (await Engine(body).GetModels()).Count);

        int pid = int.Parse((await File.ReadAllTextAsync(temp.Combine("pid.txt"))).Trim());

        for (int attempt = 0; attempt < 100 && Directory.Exists($"/proc/{pid}"); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.False(Directory.Exists($"/proc/{pid}"));
    }

    [Theory]
    [InlineData("""{"type":"control_response","response":{"request_id":"liveai-models","response":{}}}""")]
    [InlineData("""{"type":"control_response","response":{"request_id":"liveai-models","response":{"models":5}}}""")]
    [InlineData("""{"type":"control_response","response":{"request_id":"liveai-models","response":{"models":[{"displayName":"x"}]}}}""")]
    public async Task GetModels_GivesNoneWhenTheAnswerHasNoUsableModels(string json)
        => Assert.Empty(await Engine(Answer(json)).GetModels());

    [Fact]
    public async Task GetModels_GivesNoneWhenTheToolFailsOrEndsWithoutAnswering()
    {
        Assert.Empty(await Engine("IFS= read -r line\necho broken >&2\nexit 1").GetModels());
        Assert.Empty(await new ClaudeEngine(temp.Combine("not-installed")).GetModels());
    }

    [Fact]
    public async Task GetModels_GivesNoneWhenTheToolDoesNotAnswerInTime()
        => Assert.Empty(await Engine("cat > /dev/null\nsleep 120", TimeSpan.FromMilliseconds(300)).GetModels());

    [Fact]
    public async Task GetModels_StopsWhenTheCallerCancels()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine("cat > /dev/null\nsleep 120").GetModels(cancellation: cancellation.Token));
    }
}
