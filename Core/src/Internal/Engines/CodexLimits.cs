namespace Markwardt.LiveAi;

/// <summary>
/// Reads how much of a ChatGPT subscription Codex has used, from its app server and without sending a prompt.
/// </summary>
/// <param name="command">The command that starts Codex.</param>
/// <param name="configFolder">The folder Codex keeps its state in, or <see langword="null" /> for its default.</param>
internal sealed class CodexLimits(string command, string? configFolder)
{
    private readonly ITurnRunner runner = new TurnRunner();

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

    private readonly string[] requests =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"Markwardt.LiveAi","version":"1"}}}""",
        """{"jsonrpc":"2.0","method":"initialized"}""",
        """{"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read"}"""
    ];


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

        try
        {
            await foreach (string line in runner.Exchange("Codex", CodexStart(), requests, false, limit.Token))
            {
                if (CodexAnswer(line) is { } answer)
                {
                    return answer;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new PromptSessionException("Codex did not report its usage in time.");
        }

        throw new PromptSessionException("Codex stopped before it reported its usage.");
    }

    private ProcessStartInfo CodexStart()
    {
        ProcessStartInfo start = new(command) { WorkingDirectory = Path.GetTempPath() };
        start.ArgumentList.Add("app-server");

        if (configFolder is not null)
        {
            start.Environment["CODEX_HOME"] = configFolder;
        }

        return start;
    }

    private IReadOnlyList<PromptWindow>? CodexAnswer(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int number) || number != 2)
            {
                return null;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                throw new PromptSessionException(error.TryGetProperty("message", out JsonElement message) && message.GetString() is { Length: > 0 } text ? text : "Codex could not report its usage.");
            }

            DateTimeOffset readAt = DateTimeOffset.UtcNow;
            List<PromptWindow> windows = [];

            if (root.TryGetProperty("result", out JsonElement result) && result.TryGetProperty("rateLimits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object)
            {

                foreach (string name in (string[])["primary", "secondary"])
                {
                    if (Window(limits, name, readAt) is { } window)
                    {
                        windows.Add(window);
                    }
                }
            }

            return windows.Count == 0 ? throw new PromptSessionException("Codex reported no subscription limits. It needs a ChatGPT login: an API key has none.") : windows;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private PromptWindow? Window(JsonElement limits, string name, DateTimeOffset readAt)
    {
        if (!limits.TryGetProperty(name, out JsonElement window) || window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("usedPercent", out JsonElement used) || !window.TryGetProperty("windowDurationMins", out JsonElement minutes) || !window.TryGetProperty("resetsAt", out JsonElement at))
        {
            return null;
        }

        TimeSpan length = TimeSpan.FromMinutes(minutes.GetDouble());
        return new PromptWindow { Name = Describe(length), Used = used.GetDouble() / 100, Length = length, ReadAt = readAt, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(at.GetInt64()) };
    }

    private string Describe(TimeSpan length)
        => length.TotalDays == 7 ? "Week" : length.TotalDays == 30 ? "Month" : length.TotalDays == 1 ? "Day" : length.TotalDays % 1 == 0 ? $"{length.TotalDays} days" : length.TotalHours == 1 ? "Hour" : length.TotalHours % 1 == 0 ? $"{length.TotalHours} hours" : $"{length.TotalMinutes} minutes";
}
