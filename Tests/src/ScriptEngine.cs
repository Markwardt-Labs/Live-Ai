namespace Markwardt.LiveAi.Tests;

public sealed class ScriptEngine(string script) : IPromptEngine
{
    public List<PromptEngineStart> Starts { get; } = [];

    public List<string> Deleted { get; } = [];

    public List<string> Accepted { get; } = [];

    public bool TakesImages { get; set; }

    public string Identifier => "Script Tool";

    public ProcessStartInfo CreateStart(PromptEngineStart start)
    {
        Starts.Add(start);
        ProcessStartInfo info = new(script) { WorkingDirectory = start.WorkingFolder };

        foreach (string argument in (string[])["--model", start.Capability.ToString(), "--reasoning", start.Reasoning.ToString(), .. start.AiSessionId is null ? (string[])[] : ["--resume", start.AiSessionId], .. start.ForkedFrom is null ? (string[])[] : ["--fork", start.ForkedFrom], .. start.Files.SelectMany(file => new[] { "--file", file })])
        {
            info.ArgumentList.Add(argument);
        }

        foreach (PromptEngineServer server in start.Servers.Where(server => server.BearerTokenVariable is not null && server.BearerToken is not null))
        {
            info.Environment[server.BearerTokenVariable!] = server.BearerToken;
        }

        if (start.ApiKey is not null)
        {
            info.Environment["SCRIPT_API_KEY"] = start.ApiKey;
        }

        info.Environment["SCRIPT_INSTRUCTIONS"] = start.Instructions;
        return info;
    }

    public IReadOnlyList<PromptEngineEvent> Parse(string line)
        => line.Split(' ', 2) switch
        {
            ["SESSION", string id] => [new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = id }],
            ["REPLY", string text] => [new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = text }],
            ["FAIL", string text] => [new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = text }],
            ["DONE"] => [new PromptEngineEvent { Kind = PromptEngineEventKind.Finished }],
            _ => []
        };

    public bool Accepts(string mediaType)
    {
        Accepted.Add(mediaType);
        return TakesImages && mediaType.StartsWith("image/", StringComparison.Ordinal);
    }

    public Task DeleteSession(string aiSessionId, CancellationToken cancellation = default)
    {
        Deleted.Add(aiSessionId);
        return Task.CompletedTask;
    }
}
