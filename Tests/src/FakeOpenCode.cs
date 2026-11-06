namespace Markwardt.LiveAi.Tests;

/// <summary>
/// Writes a script that stands in for <c>opencode</c>: as <c>serve --stdio</c> it reports the address of a <see cref="FakeOpenCodeServer" /> and waits until its standard input closes; as <c>session export</c>, <c>session delete</c>, <c>api</c> and <c>run</c> it answers from files; and it keeps its arguments, environment and configuration for the test to read.
/// </summary>
/// <param name="temp">The folder the script and its logs are in.</param>
public sealed class FakeOpenCode(TempFolder temp)
{
    /// <summary>
    /// Writes the script.
    /// </summary>
    /// <param name="server">The server to report, or <see langword="null" /> for a script that has none. A script with a server also lists the models <c>fake/model</c> and <c>fake/big</c>.</param>
    /// <param name="name">The file name of the script.</param>
    /// <param name="export">What <c>session export</c> prints, or <see langword="null" /> for a session that is not found.</param>
    /// <param name="fork">What <c>api</c> prints, or <see langword="null" /> for nothing.</param>
    /// <returns>The path of the script.</returns>
    public string Script(FakeOpenCodeServer? server = null, string name = "opencode", string? export = null, string? fork = null)
    {
        if (server is not null)
        {
            File.WriteAllText(temp.Combine("server-url.txt"), server.Url);
            File.WriteAllText(temp.Combine("default-models.json"), """{"data":[{"providerID":"fake","modelID":"model","id":"model","limit":{"context":32000,"output":4000},"variants":[{"id":"low"},{"id":"medium"},{"id":"high"}]},{"providerID":"fake","modelID":"big","id":"big","limit":{"context":64000,"output":8000},"variants":[{"id":"low"},{"id":"high"},{"id":"max"}]}]}""");
        }

        if (export is not null)
        {
            File.WriteAllText(temp.Combine("export.json"), export);
        }

        if (fork is not null)
        {
            File.WriteAllText(temp.Combine("api-response.json"), fork);
        }

        return temp.Script(name, """
            if [ "$4" != "/api/model" ]; then printf '%s\n' "$@" > "@TEMP@/opencode-args-$1.txt"; fi
            printf '%s' "$OPENCODE_CONFIG_CONTENT" > "@TEMP@/opencode-config.txt"
            printf '%s' "$PWD" > "@TEMP@/opencode-folder.txt"
            if [ -n "$LIVEAI_TOOLS_TOKEN" ]; then printf '%s' "$LIVEAI_TOOLS_TOKEN" > "@TEMP@/opencode-token.txt"; fi
            if [ -n "$OPENCODE_SERVER_PASSWORD" ]; then printf '%s' "$OPENCODE_SERVER_PASSWORD" > "@TEMP@/opencode-password.txt"; fi
            case "$1" in
            serve)
              echo $$ >> "@TEMP@/pids.txt"
              printf '{"url":"%s"}\n' "$(cat "@TEMP@/server-url.txt")"
              cat > /dev/null
              exit 0;;
            session)
              case "$2" in
              export) if [ -f "@TEMP@/export.json" ]; then cat "@TEMP@/export.json"; else echo "Session not found: $4"; fi;;
              delete) echo "deleted";;
              esac
              exit 0;;
            api)
              if [ "$4" = "/api/model" ] && [ -f "@TEMP@/default-models.json" ]; then cat "@TEMP@/default-models.json"
              elif [ -f "@TEMP@/api-response.json" ]; then cat "@TEMP@/api-response.json"; fi
              exit 0;;
            run)
              cat > "@TEMP@/opencode-run-prompt.txt"
              echo '{"type":"step_start","sessionID":"ses_run","part":{"type":"step-start"}}'
              echo '{"type":"text","sessionID":"ses_run","part":{"type":"text","text":"1"}}'
              echo '{"type":"step_finish","sessionID":"ses_run","part":{"type":"step-finish","reason":"stop","tokens":{"input":3,"output":1,"cache":{"read":0,"write":0}}}}'
              exit 0;;
            esac
            """.Replace("@TEMP@", temp.Path));
    }

    /// <summary>
    /// Reads the configuration the script was last started with.
    /// </summary>
    /// <returns>The inline configuration as JSON.</returns>
    public async Task<JsonElement> Config()
        => JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("opencode-config.txt"))).RootElement.Clone();

    /// <summary>
    /// Reads the arguments the script was last started with for a command.
    /// </summary>
    /// <param name="command">The first argument: <c>serve</c>, <c>session</c>, <c>api</c> or <c>run</c>.</param>
    /// <returns>The arguments.</returns>
    public async Task<IReadOnlyList<string>> Arguments(string command)
        => await File.ReadAllLinesAsync(temp.Combine($"opencode-args-{command}.txt"));

    /// <summary>
    /// Reads the password the server was started with.
    /// </summary>
    /// <returns>The password.</returns>
    public async Task<string> Password()
        => await File.ReadAllTextAsync(temp.Combine("opencode-password.txt"));
}
