namespace Markwardt.LiveAi.Tests.Unit;

internal sealed class PromptSessionRecordBuilder
{
    public PromptCapability Capability { get; set; } = PromptCapability.Balanced;

    public PromptReasoning Reasoning { get; set; } = PromptReasoning.Medium;

    public string? AiSessionId { get; set; }

    public string? ForkedFrom { get; set; }

    public string? ForkedAt { get; set; }

    public string? Instructions { get; set; }

    public IReadOnlyDictionary<string, PromptMcpServer> McpServers { get; set; } = new Dictionary<string, PromptMcpServer>();

    public PromptSessionRecord Build(IPromptEngine engine)
        => new()
        {
            Id = Guid.NewGuid(),
            Engine = engine,
            Capability = Capability,
            Reasoning = Reasoning,
            WorkingFolder = "/work",
            AiSessionId = AiSessionId,
            ForkedFrom = ForkedFrom,
            ForkedAt = ForkedAt,
            Instructions = Instructions,
            McpServers = McpServers
        };
}
