namespace Markwardt.LiveAi.Tests;

/// <summary>
/// Writes a script that stands in for <c>codex app-server</c>: it answers the requests the library makes with a thread called <c>codex-thread</c>, one agent message per turn, and keeps every request it read in <c>codex-requests.txt</c> and the arguments it was started with in <c>codex-args.txt</c> (the API key in <c>codex-key.txt</c>) in the temp folder.
/// </summary>
/// <param name="temp">The folder the script and its logs are in.</param>
public sealed class FakeCodex(TempFolder temp)
{
    /// <summary>
    /// Writes the script.
    /// </summary>
    /// <param name="name">The file name of the script.</param>
    /// <param name="reply">What the agent writes in every turn.</param>
    /// <param name="turn">Shell commands that replace the notifications of a turn after the answer to <c>turn/start</c>; <c>$id</c> is the identifier of the request, <c>$tid</c> the thread, <c>$turn</c> the identifier of the turn and <c>$line</c> the request.</param>
    /// <param name="before">Shell commands that run for each turn before its notifications, with the same variables.</param>
    /// <param name="steer">Shell commands that run when input is steered into a turn, after its answer, with the same variables.</param>
    /// <param name="models">What <c>debug models</c> prints, or <see langword="null" /> for nothing; each run is counted in <c>catalog-runs.txt</c>.</param>
    /// <returns>The path of the script.</returns>
    public string Script(string name = "codex", string reply = "codex says hi", string? turn = null, string? before = null, string? steer = null, string? models = null)
    {
        if (models is not null)
        {
            File.WriteAllText(temp.Combine("codex-models.json"), models);
        }

        return temp.Script(name, """
            if [ "$1" = "debug" ]; then echo x >> "$PWD/catalog-runs.txt"; cat "$PWD/codex-models.json" 2>/dev/null; exit 0; fi
            mkdir -p "$CODEX_HOME/sessions/2026/01/01"
            echo $$ >> "$PWD/pids.txt"
            printf '%s\n' "$@" > "$PWD/codex-args.txt"
            if [ -n "$CODEX_API_KEY" ]; then printf '%s' "$CODEX_API_KEY" > "$PWD/codex-key.txt"; fi
            while IFS= read -r line; do
              printf '%s\n' "$line" >> "$PWD/codex-requests.txt"
              id=$(printf '%s' "$line" | sed -n 's/^{"id":\([0-9][0-9]*\),.*/\1/p')
              tid=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
              case "$line" in
              *'"method":"initialize"'*) echo "{\"id\":$id,\"result\":{}}";;
              *'"method":"thread/start"'*)
                : > "$CODEX_HOME/sessions/2026/01/01/rollout-2026-01-01T00-00-00-codex-thread.jsonl"
                echo "{\"id\":$id,\"result\":{\"thread\":{\"id\":\"codex-thread\"}}}";;
              *'"method":"thread/resume"'*)
                echo "{\"id\":$id,\"result\":{\"thread\":{\"id\":\"$tid\"}}}"
                echo "{\"method\":\"thread/tokenUsage/updated\",\"params\":{\"threadId\":\"$tid\",\"turnId\":\"earlier\",\"tokenUsage\":{\"total\":{\"totalTokens\":90},\"last\":{\"totalTokens\":40,\"inputTokens\":30,\"cachedInputTokens\":0,\"outputTokens\":10},\"modelContextWindow\":1000}}}";;
              *'"method":"thread/fork"'*)
                forks=$(( $(cat "$PWD/fork-count" 2>/dev/null || echo 0) + 1 ))
                echo "$forks" > "$PWD/fork-count"
                : > "$CODEX_HOME/sessions/2026/01/01/rollout-2026-01-01T00-00-00-codex-fork-$forks.jsonl"
                echo "{\"id\":$id,\"result\":{\"thread\":{\"id\":\"codex-fork-$forks\"}}}";;
              *'"method":"thread/inject_items"'*) echo "{\"id\":$id,\"result\":{}}";;
              *'"method":"thread/compact/start"'*)
                echo "{\"id\":$id,\"result\":{}}"
                echo "{\"method\":\"thread/tokenUsage/updated\",\"params\":{\"threadId\":\"$tid\",\"turnId\":\"compaction\",\"tokenUsage\":{\"total\":{\"totalTokens\":50},\"last\":{\"totalTokens\":40,\"inputTokens\":30,\"cachedInputTokens\":0,\"outputTokens\":10},\"modelContextWindow\":1000}}}"
                echo "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"$tid\",\"turn\":{\"id\":\"compaction\",\"status\":\"completed\"}}}";;
              *'"method":"turn/steer"'*)
                turn="turn-$(cat "$PWD/turn-count")"
                echo "{\"id\":$id,\"result\":{\"turnId\":\"$turn\"}}"
                @STEER@;;
              *'"method":"turn/interrupt"'*) : > "$PWD/interrupted"; echo "{\"id\":$id,\"result\":{}}";;
              *'"method":"turn/start"'*)
                count=$(( $(cat "$PWD/turn-count" 2>/dev/null || echo 0) + 1 ))
                echo "$count" > "$PWD/turn-count"
                turn="turn-$count"
                if [ -f "$PWD/slow-start" ]; then sleep 0.6; fi
                echo "{\"id\":$id,\"result\":{\"turn\":{\"id\":\"$turn\"}}}"
                : > "$PWD/turn-started"
                @BEFORE@
                @TURN@;;
              esac
            done
            """.Replace("@BEFORE@", before ?? ":").Replace("@TURN@", turn ?? Notifications(reply)).Replace("@STEER@", steer ?? ":"));
    }

    private string Notifications(string reply)
    {
        string[] lines =
        [
            JsonSerializer.Serialize(new { method = "item/agentMessage/delta", @params = new { threadId = "@TID@", turnId = "@TURN@", itemId = "m", delta = reply } }),
            JsonSerializer.Serialize(new { method = "item/completed", @params = new { threadId = "@TID@", turnId = "@TURN@", item = new { type = "agentMessage", id = "m", text = reply } } }),
            """{"method":"thread/tokenUsage/updated","params":{"threadId":"@TID@","turnId":"@TURN@","tokenUsage":{"total":{"totalTokens":30},"last":{"totalTokens":15,"inputTokens":10,"cachedInputTokens":4,"outputTokens":5},"modelContextWindow":1000}}}""",
            """{"method":"turn/completed","params":{"threadId":"@TID@","turn":{"id":"@TURN@","status":"completed"}}}"""
        ];

        return string.Join('\n', lines.Select(line => $"printf '%s\\n' '{line.Replace("'", "'\\''")}' | sed \"s/@TID@/$tid/g; s/@TURN@/$turn/g\""));
    }

    /// <summary>
    /// Reads the requests the script was sent, in order.
    /// </summary>
    /// <returns>One JSON request per entry.</returns>
    public async Task<IReadOnlyList<JsonElement>> Requests()
        => File.Exists(temp.Combine("codex-requests.txt")) ? [.. (await File.ReadAllLinesAsync(temp.Combine("codex-requests.txt"))).Select(line => JsonDocument.Parse(line).RootElement.Clone())] : [];

    /// <summary>
    /// Reads the requests of one method, in order.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The <c>params</c> of each request.</returns>
    public async Task<IReadOnlyList<JsonElement>> Parameters(string method)
        => [.. (await Requests()).Where(request => request.TryGetProperty("method", out JsonElement found) && found.GetString() == method).Select(request => request.TryGetProperty("params", out JsonElement parameters) ? parameters : default)];
}
