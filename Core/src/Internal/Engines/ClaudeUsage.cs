namespace Markwardt.LiveAi;

/// <summary>
/// Reads how much of a Claude Code subscription is used, with the tool's own <c>/usage</c> command and without sending a prompt.
/// </summary>
/// <param name="command">The command that starts Claude Code.</param>
/// <param name="configFolder">The folder Claude Code keeps its state in, or <see langword="null" /> for its default.</param>
internal sealed class ClaudeUsage(string command, string? configFolder)
{
    private readonly ITurnRunner runner = new TurnRunner();

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

    private readonly Regex session = new(@"Current session:\s*(?<percent>\d+(?:\.\d+)?)%\s*used(?:\s*[·\-]\s*resets\s*(?<reset>.+))?", RegexOptions.CultureInvariant);

    private readonly Regex models = new(@"Current week \((?<name>[^)]+)\):\s*(?<percent>\d+(?:\.\d+)?)%\s*used(?:\s*[·\-]\s*resets\s*(?<reset>.+))?", RegexOptions.CultureInvariant);

    private readonly Regex week = new(@"Current week \(all models\):\s*(?<percent>\d+(?:\.\d+)?)%\s*used(?:\s*[·\-]\s*resets\s*(?<reset>.+))?", RegexOptions.CultureInvariant);

    private readonly Regex reset = new(@"^(?:(?<month>[A-Za-z]{3,9})\s+(?<day>\d{1,2}),?\s+)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<half>am|pm)\s*(?:\((?<zone>[^)]+)\))?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);


    /// <summary>
    /// Reads the limit windows.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The windows.</returns>
    /// <exception cref="PromptSessionException">The tool reported no limits, did not answer in time or could not be started.</exception>
    public async Task<IReadOnlyList<PromptWindow>> Read(CancellationToken cancellation = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        string? text = null;

        try
        {
            await foreach (string line in runner.Exchange("Claude Code", ClaudeStart(), [], true, limit.Token))
            {
                text = ClaudeResult(line) ?? text;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new PromptSessionException("Claude Code did not report its usage in time.");
        }

        IReadOnlyList<PromptWindow>? windows = text is null ? null : ParseClaude(text);
        return windows ?? throw new PromptSessionException("Claude Code reported no subscription limits. It needs a subscription login: an API key has none.");
    }

    private ProcessStartInfo ClaudeStart()
    {
        ProcessStartInfo start = new(command) { WorkingDirectory = Path.GetTempPath() };

        foreach (string argument in (string[])["-p", "/usage", "--output-format", "json", "--strict-mcp-config", "--no-session-persistence"])
        {
            start.ArgumentList.Add(argument);
        }

        if (configFolder is not null)
        {
            start.Environment["CLAUDE_CONFIG_DIR"] = configFolder;
        }

        return start;
    }

    private string? ClaudeResult(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IReadOnlyList<PromptWindow>? ParseClaude(string text)
    {
        Match current = session.Match(text);
        Match weekly = week.Match(text);

        if (!current.Success && !weekly.Success)
        {
            return null;
        }

        DateTimeOffset readAt = DateTimeOffset.UtcNow;
        List<PromptWindow> windows = [];

        if (Window("Session", TimeSpan.FromHours(5), readAt, current) is { } now)
        {
            windows.Add(now);
        }

        if (Window("Week", TimeSpan.FromDays(7), readAt, weekly) is { } all)
        {
            windows.Add(all);
        }

        foreach (Match match in models.Matches(text).Where(match => !match.Groups["name"].Value.Equals("all models", StringComparison.OrdinalIgnoreCase)).DistinctBy(match => match.Groups["name"].Value.Trim()))
        {
            if (Window($"Week ({match.Groups["name"].Value.Trim()})", TimeSpan.FromDays(7), readAt, match) is { } model)
            {
                windows.Add(model);
            }
        }

        return windows;
    }

    private PromptWindow? Window(string name, TimeSpan length, DateTimeOffset readAt, Match match)
        => Fraction(match) is { } used ? new PromptWindow { Name = name, Used = used, Length = length, ReadAt = readAt, ResetsAt = Reset(match) } : null;

    private double? Fraction(Match match)
        => match.Success ? double.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture) / 100 : null;

    private DateTimeOffset? Reset(Match match)
    {
        if (!match.Success || !match.Groups["reset"].Success || reset.Match(match.Groups["reset"].Value.Trim()) is not { Success: true } time)
        {
            return null;
        }

        TimeZoneInfo zone = Zone(time.Groups["zone"].Value);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTime today = TimeZoneInfo.ConvertTime(now, zone).DateTime.Date;
        int hour = int.Parse(time.Groups["hour"].Value, CultureInfo.InvariantCulture) % 12 + (time.Groups["half"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        int minute = time.Groups["minute"].Success ? int.Parse(time.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;

        try
        {
            if (time.Groups["month"].Success)
            {
                if (!DateTime.TryParseExact($"{time.Groups["month"].Value} {time.Groups["day"].Value}", ["MMM d", "MMMM d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
                {
                    return null;
                }

                DateTimeOffset at = At(zone, new DateTime(today.Year, day.Month, day.Day, hour, minute, 0));
                return at < now.AddDays(-1) ? At(zone, new DateTime(today.Year + 1, day.Month, day.Day, hour, minute, 0)) : at;
            }

            DateTimeOffset soon = At(zone, today.AddHours(hour).AddMinutes(minute));
            return soon < now ? At(zone, today.AddDays(1).AddHours(hour).AddMinutes(minute)) : soon;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private TimeZoneInfo Zone(string name)
    {
        try
        {
            return name.Length > 0 ? TimeZoneInfo.FindSystemTimeZoneById(name) : TimeZoneInfo.Local;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private DateTimeOffset At(TimeZoneInfo zone, DateTime local)
        => new(local, zone.GetUtcOffset(local));
}
