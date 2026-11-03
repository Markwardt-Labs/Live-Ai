namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptMcpServerTests
{
    [Fact]
    public void Stdio_DescribesAProgramWithItsArguments()
    {
        PromptMcpServer server = PromptMcpServer.Stdio("npx", "-y", "tool");

        Assert.Equal("npx", server.Command);
        Assert.Equal(["-y", "tool"], server.Arguments);
        Assert.Null(server.Url);
        Assert.Empty(server.EnvironmentVariables);
    }

    [Fact]
    public void Http_DescribesAnAddressWithAnOptionalTokenVariable()
    {
        PromptMcpServer server = PromptMcpServer.Http("https://example.com/mcp", "TOKEN");

        Assert.Equal("https://example.com/mcp", server.Url);
        Assert.Equal("TOKEN", server.BearerTokenVariable);
        Assert.Null(server.Command);
        Assert.Null(PromptMcpServer.Http("https://example.com/mcp").BearerTokenVariable);
    }

    [Fact]
    public void Equality_ComparesTheContentOfTheCollections()
    {
        PromptMcpServer first = PromptMcpServer.Stdio("tool", "a") with { EnvironmentVariables = new Dictionary<string, string> { ["K"] = "v" } };
        PromptMcpServer same = PromptMcpServer.Stdio("tool", "a") with { EnvironmentVariables = new Dictionary<string, string> { ["K"] = "v" } };

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, PromptMcpServer.Stdio("tool", "b") with { EnvironmentVariables = first.EnvironmentVariables });
        Assert.NotEqual(first, PromptMcpServer.Stdio("tool", "a"));
        Assert.NotEqual(first, same with { EnvironmentVariables = new Dictionary<string, string> { ["K"] = "other" } });
        Assert.NotEqual(PromptMcpServer.Http("https://a"), PromptMcpServer.Http("https://a", "T"));
    }

    [Fact]
    public void ARecordWithServers_StoresAndReadsThemAsJson()
    {
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        PromptMcpServer server = PromptMcpServer.Stdio("tool", "a", "b") with { EnvironmentVariables = new Dictionary<string, string> { ["K"] = "v" } };

        PromptMcpServer read = JsonSerializer.Deserialize<PromptMcpServer>(JsonSerializer.Serialize(server, json), json)!;

        Assert.Equal(server, read);
    }

    [Fact]
    public void RecordsOfSessions_AreEqualWhenTheirServersAndInstructionsAre()
    {
        PromptSessionRecord first = new PromptSessionRecordBuilder { Instructions = "x", McpServers = new Dictionary<string, PromptMcpServer> { ["a"] = PromptMcpServer.Http("https://a") } }.Build(new ClaudeEngine());
        PromptSessionRecord same = first with { McpServers = new Dictionary<string, PromptMcpServer> { ["a"] = PromptMcpServer.Http("https://a") } };

        Assert.Equal(first, same);
        Assert.NotEqual(first, first with { Instructions = "y" });
        Assert.NotEqual(first, first with { ForkedFrom = "o" });
        Assert.NotEqual(first, first with { McpServers = new Dictionary<string, PromptMcpServer>() });
        Assert.NotEqual(first, first with { McpServers = new Dictionary<string, PromptMcpServer> { ["a"] = PromptMcpServer.Http("https://b") } });
    }

    [Fact]
    public void SameProcess_OfAToolThatStartsWithItsModel_NeedsANewProcessWhenTheModelOrItsSettingsChange()
    {
        PromptSessionRecord record = new PromptSessionRecordBuilder().Build(new OpenCodeEngine());

        Assert.True(record.SameProcess(record with { Model = record.Model! with { } }));
        Assert.False(record.SameProcess(record with { Model = new PromptModel { Id = "other" } }));
        Assert.False(record.SameProcess(record with { ModelSettings = new Dictionary<string, string> { ["effort"] = "high" } }));
    }

    [Fact]
    public void SameProcess_IgnoresTheSessionAndTheModelBecauseTheyChangeWhileTheProcessRunsButNotWhatTheToolIsGiven()
    {
        PromptSessionRecord record = new PromptSessionRecordBuilder { Instructions = "x" }.Build(new ClaudeEngine());

        Assert.True(record.SameProcess(record with { AiSessionId = "other", ForkedFrom = "o", ForkedAt = "m" }));
        Assert.True(record.SameProcess(record with { Model = new PromptModel { Id = "opus" } }));
        Assert.True(record.SameProcess(record with { ModelSettings = new Dictionary<string, string> { ["effort"] = "max" } }));
        Assert.False(record.SameProcess(record with { Instructions = "y" }));
        Assert.False(record.SameProcess(record with { McpServers = new Dictionary<string, PromptMcpServer> { ["a"] = PromptMcpServer.Http("https://a") } }));
    }
}
