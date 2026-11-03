namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class EngineWindowTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptProvider Provider(IPromptEngine engine)
        => new PromptProvider(engine, temp.Combine("data"));

    private ClaudeEngine Claude(string? script = null)
        => new ClaudeEngine(script is null ? temp.Combine("missing-claude") : temp.Script("claude", script), temp.Combine("claude-state"));

    private CodexEngine Codex(string? script = null)
        => new CodexEngine(script is null ? temp.Combine("missing-codex") : temp.Script("codex", script), temp.Combine("codex-state"));

    private string Usage(string text)
        => $"printf '%s\\n' \"$@\" > \"$PWD/claude-args.txt\"\ncat <<'EOF'\n{JsonSerializer.Serialize(new { type = "result", is_error = false, result = text })}\nEOF";

    private IReadOnlyList<PromptWindow> Parsed(string text)
        => Claude(Usage(text)).GetWindows().GetAwaiter().GetResult();

    private PromptWindow? Find(IReadOnlyList<PromptWindow> windows, string name)
        => windows.SingleOrDefault(window => window.Name == name);

    private string CodexAnswers(string limits)
        => $"printf '%s\\n' \"$@\" > \"$PWD/codex-args.txt\"\nwhile IFS= read -r line; do\ncase \"$line\" in\n*'\"id\":1'*) echo '{{\"id\":1,\"result\":{{}}}}';;\n*'\"id\":2'*) echo '{limits}';;\nesac\ndone";

    [Fact]
    public async Task Claude_ReadsTheUsageCommandWithoutSendingAPrompt()
    {
        ClaudeEngine claude = Claude(Usage("You are using your subscription\n\nCurrent session: 20% used · resets Oct 26, 6:50pm (America/Chicago)\nCurrent week (all models): 23% used · resets Oct 31, 2am (America/Chicago)\nCurrent week (Fable): 0% used · resets Oct 31, 2am (America/Chicago)"));

        IReadOnlyList<PromptWindow> windows = await claude.GetWindows();

        PromptWindow session = Find(windows, "Session")!;
        PromptWindow week = Find(windows, "Week")!;
        Assert.Equal(["Session", "Week", "Week (Fable)"], windows.Select(window => window.Name));
        Assert.Equal((0.2, TimeSpan.FromHours(5)), (session.Used, session.Length));
        Assert.Equal((0.23, TimeSpan.FromDays(7)), (week.Used, week.Length));
        Assert.Equal(0, Find(windows, "Week (Fable)")!.Used);
        Assert.Equal(week.ResetsAt, Find(windows, "Week (Fable)")!.ResetsAt);
        Assert.Equal(TimeSpan.FromHours(-5), session.ResetsAt!.Value.Offset);
        Assert.Equal((6 + 12, 50), (session.ResetsAt.Value.Hour, session.ResetsAt.Value.Minute));
        Assert.Equal((10, 31, 2, 0), (week.ResetsAt!.Value.Month, week.ResetsAt.Value.Day, week.ResetsAt.Value.Hour, week.ResetsAt.Value.Minute));
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        Assert.Equal(["-p", "/usage"], arguments.Take(2));
        Assert.Contains("--no-session-persistence", arguments);
    }

    [Fact]
    public void Claude_ReadsDecimalPercentsAndADateAsTheNextOneInTheNamedZone()
    {
        DateTime day = DateTime.UtcNow.Date.AddDays(10);

        IReadOnlyList<PromptWindow> windows = Parsed($"Current session: 12.5% used · resets {day.ToString("MMM d", CultureInfo.InvariantCulture)}, 3:05pm (UTC)");

        PromptWindow session = Assert.Single(windows);
        Assert.Equal(0.125, session.Used);
        Assert.Equal(new DateTimeOffset(day.AddHours(15).AddMinutes(5), TimeSpan.Zero), session.ResetsAt);
    }

    [Fact]
    public void Claude_ReadsEverySingleModelWeeklyLimitAndKeepsTheFirstOfAName()
    {
        IReadOnlyList<PromptWindow> windows = Parsed("Current week (all models): 10% used\nCurrent week (Opus): 42.5% used · resets 11pm (UTC)\nCurrent week (Sonnet): 7% used\nCurrent week (Opus): 99% used");

        Assert.Equal(["Week", "Week (Opus)", "Week (Sonnet)"], windows.Select(window => window.Name));
        Assert.Equal(0.425, Find(windows, "Week (Opus)")!.Used);
        Assert.NotNull(Find(windows, "Week (Opus)")!.ResetsAt);
        Assert.Null(Find(windows, "Week (Sonnet)")!.ResetsAt);
        Assert.All(windows, window => Assert.Equal(TimeSpan.FromDays(7), window.Length));
        Assert.DoesNotContain(Parsed("Current session: 1% used"), window => window.Name.StartsWith("Week", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_ReadsATimeWithoutADateAsTheNextTimeItOccurs()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;

        IReadOnlyList<PromptWindow> windows = Parsed("Current session: 5% used · resets 11:59pm (UTC)");

        Assert.Equal((23, 59), (Find(windows, "Session")!.ResetsAt!.Value.Hour, Find(windows, "Session")!.ResetsAt!.Value.Minute));
        Assert.InRange(Find(windows, "Session")!.ResetsAt!.Value, before, before.AddHours(24).AddMinutes(1));
    }

    [Fact]
    public void Claude_ReadsMidnightAndNoonOnTheTwelveHourClock()
    {
        Assert.Equal(0, Find(Parsed("Current session: 5% used · resets 12am (UTC)"), "Session")!.ResetsAt!.Value.Hour);
        Assert.Equal(12, Find(Parsed("Current session: 5% used · resets 12:30pm (UTC)"), "Session")!.ResetsAt!.Value.Hour);
    }

    [Fact]
    public void Claude_WithAnUnknownZoneAUnreadableResetOrNone_ReportsThePercentAndLeavesTheResetOut()
    {
        Assert.NotNull(Find(Parsed("Current session: 5% used · resets 3pm (Nowhere/Land)"), "Session")!.ResetsAt);
        Assert.Null(Find(Parsed("Current session: 5% used · resets sometime soon"), "Session")!.ResetsAt);
        Assert.Null(Find(Parsed("Current session: 5% used"), "Session")!.ResetsAt);
        Assert.Null(Find(Parsed("Current session: 5% used · resets Foo 40, 3pm (UTC)"), "Session")!.ResetsAt);
        Assert.Equal(0.05, Find(Parsed("Current session: 5% used"), "Session")!.Used);
    }

    [Fact]
    public async Task Claude_WithoutLimitsInTheOutput_Fails()
    {
        ClaudeEngine claude = Claude(Usage("You are using an API key. /usage is only for subscriptions."));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => claude.GetWindows());

        Assert.Contains("no subscription limits", exception.Message);
    }

    [Fact]
    public async Task Claude_WithoutAnyJsonOutput_Fails()
    {
        ClaudeEngine claude = Claude("echo not json");

        await Assert.ThrowsAsync<PromptSessionException>(() => claude.GetWindows());
    }

    [Fact]
    public async Task Claude_WhenTheToolFails_ReportsItsError()
    {
        ClaudeEngine claude = Claude("echo logged out >&2\nexit 1");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => claude.GetWindows());

        Assert.Equal("logged out", exception.Message);
    }

    [Fact]
    public async Task Claude_WhenTheToolCannotBeStarted_Fails()
    {
        ClaudeEngine claude = Claude();

        await Assert.ThrowsAsync<PromptToolStartException>(() => claude.GetWindows());
    }

    [Fact]
    public async Task Codex_AsksTheAppServerForTheRateLimits()
    {
        CodexEngine codex = Codex(CodexAnswers("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":22,"windowDurationMins":43200,"resetsAt":1795542194},"secondary":{"usedPercent":40.5,"windowDurationMins":10080,"resetsAt":1793430000}}}}"""));

        IReadOnlyList<PromptWindow> windows = await codex.GetWindows();

        Assert.Equal(
            [
                new PromptWindow { Name = "Month", Used = 0.22, Length = TimeSpan.FromDays(30), ReadAt = windows[0].ReadAt, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1795542194) },
                new PromptWindow { Name = "Week", Used = 0.405, Length = TimeSpan.FromDays(7), ReadAt = windows[0].ReadAt, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1793430000) }
            ],
            windows);
        Assert.Equal(["app-server"], await File.ReadAllLinesAsync(temp.Combine("codex-args.txt")));
    }

    [Fact]
    public async Task Codex_NamesAWindowByItsLength()
    {
        CodexEngine codex = Codex(CodexAnswers("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":1440,"resetsAt":1795542194},"secondary":{"usedPercent":2,"windowDurationMins":4320,"resetsAt":1795542194}}}}"""));
        CodexEngine hours = new(temp.Script("codex-hours", CodexAnswers("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":60,"resetsAt":1795542194},"secondary":{"usedPercent":2,"windowDurationMins":90,"resetsAt":1795542194}}}}""")), temp.Combine("codex-state"));

        Assert.Equal(["Day", "3 days"], (await codex.GetWindows()).Select(window => window.Name));
        Assert.Equal(["Hour", "90 minutes"], (await hours.GetWindows()).Select(window => window.Name));
    }

    [Fact]
    public async Task Codex_WithOneWindow_LeavesTheOtherOut()
    {
        CodexEngine codex = Codex(CodexAnswers("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":22,"windowDurationMins":300,"resetsAt":1795542194},"secondary":null}}}"""));

        IReadOnlyList<PromptWindow> windows = await codex.GetWindows();

        PromptWindow window = Assert.Single(windows);
        Assert.Equal(("5 hours", TimeSpan.FromHours(5)), (window.Name, window.Length));
    }

    [Fact]
    public async Task Codex_WithoutLimits_Fails()
    {
        CodexEngine codex = Codex(CodexAnswers("""{"id":2,"result":{"rateLimits":{"primary":null,"secondary":null}}}"""));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => codex.GetWindows());

        Assert.Contains("no subscription limits", exception.Message);
    }

    [Fact]
    public async Task Codex_WhenTheServerAnswersWithAnError_ReportsIt()
    {
        CodexEngine codex = Codex(CodexAnswers("""{"id":2,"error":{"code":-1,"message":"not logged in"}}"""));

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => codex.GetWindows());

        Assert.Equal("not logged in", exception.Message);
    }

    [Fact]
    public async Task Codex_IgnoresLinesThatAreNotTheAnswer()
    {
        CodexEngine codex = Codex("echo 'a warning, not json'\necho '{\"method\":\"configWarning\"}'\necho '{\"id\":7,\"result\":{}}'\n" + CodexAnswers("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":60,"resetsAt":1795542194}}}}"""));

        Assert.Equal(0.01, Assert.Single((await codex.GetWindows())).Used);
    }

    [Fact]
    public async Task Codex_WhenTheServerStopsWithoutAnswering_Fails()
    {
        CodexEngine codex = Codex("exit 0");

        PromptSessionException exception = await Assert.ThrowsAsync<PromptSessionException>(() => codex.GetWindows());

        Assert.Contains("stopped", exception.Message);
    }

    [Fact]
    public async Task Codex_WhenTheToolCannotBeStarted_Fails()
    {
        CodexEngine codex = Codex();

        await Assert.ThrowsAsync<PromptToolStartException>(() => codex.GetWindows());
    }

    [Fact]
    public async Task ASessionsWindowLimit_ReadsThroughItsProvider()
    {
        string usage = Usage("Current session: 95% used · resets 11pm (UTC)");
        ClaudeEngine claude = Claude("case \"$*\" in *'/usage'*) ;; *) cat > /dev/null; sleep 20;; esac\n" + usage);
        await using IPromptSession session = temp.Track(await Provider(claude).Create(TestModels.Options));
        session.Configuration.Limits = session.Configuration.Limits with { WindowLimit = new PromptWindowLimit { Reached = windows => windows.Any(window => window.Used > 0.9) } };

        PromptLimitException exception = await Assert.ThrowsAsync<PromptLimitException>(() => session.Request("go"));

        Assert.Equal(PromptLimit.Window, exception.Limit);
    }
}
