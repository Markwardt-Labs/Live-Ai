namespace Markwardt.LiveAi;

/// <summary>
/// Turns what the library knows about a session into what a engine is given to start its tool.
/// </summary>
internal static class PromptStarts
{
    extension(PromptSessionRecord record)
    {
        /// <summary>
        /// Describes what a process is started for.
        /// </summary>
        /// <param name="apiKey">The API key to run with, or <see langword="null" /> to use the configured subscription.</param>
        /// <param name="schema">The formatted schema the answer must follow, or <see langword="null" /> for an answer in plain text.</param>
        /// <param name="tools">Where the caller's tools are offered, or <see langword="null" /> for none.</param>
        /// <param name="files">The paths of the attachments that were written as files, or none.</param>
        /// <returns>The description.</returns>
        public PromptEngineStart ToStart(string? apiKey, string? schema, ToolEndpoint? tools, IReadOnlyList<string>? files = null)
            => new()
            {
                Model = record.Model?.Id,
                Settings = record.ModelSettings,
                WorkingFolder = record.WorkingFolder,
                AiSessionId = record.AiSessionId,
                ForkedFrom = record.AiSessionId is null ? record.ForkedFrom : null,
                ForkedAt = record.AiSessionId is null ? record.ForkedAt : null,
                ApiKey = apiKey,
                Instructions = tools.Guidance(record),
                Schema = schema is null ? null : JsonDocument.Parse(schema).RootElement.Clone(),
                Servers = [.. Servers(record, tools)],
                Files = files ?? []
            };
    }

    private static IEnumerable<PromptEngineServer> Servers(PromptSessionRecord record, ToolEndpoint? tools)
    {
        if (tools is not null)
        {
            yield return new PromptEngineServer { Name = tools.ServerName, Url = tools.Url, BearerTokenVariable = tools.TokenVariable, BearerToken = tools.Token, Tools = tools.Names };
        }

        foreach ((string name, PromptMcpServer server) in record.McpServers)
        {
            yield return new PromptEngineServer { Name = name, Url = server.Url, BearerTokenVariable = server.BearerTokenVariable, Command = server.Command, Arguments = server.Arguments, EnvironmentVariables = server.EnvironmentVariables };
        }
    }
}
