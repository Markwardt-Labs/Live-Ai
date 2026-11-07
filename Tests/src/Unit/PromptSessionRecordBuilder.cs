namespace Markwardt.LiveAi.Tests.Unit;

internal sealed class PromptSessionRecordBuilder
{
    public PromptModel? Model { get; set; } = new() { Id = "sonnet", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high", "max"])] };

    public IReadOnlyDictionary<string, string> ModelSettings { get; set; } = new Dictionary<string, string> { ["effort"] = "high" };

    public string? AiSessionId { get; set; }

    public string? ForkedFrom { get; set; }

    public string? ForkedAt { get; set; }

    public string? Instructions { get; set; }

    public IReadOnlyDictionary<string, PromptMcpServer> McpServers { get; set; } = new Dictionary<string, PromptMcpServer>();

    public PromptSessionRecord Build(IPromptEngine engine)
        => new()
        {
            LastLoaded = TestModels.Loaded,
            Id = Guid.NewGuid(),
            Engine = engine,
            Model = Model,
            ModelSettings = ModelSettings,
            WorkingFolder = "/work",
            AiSessionId = AiSessionId,
            ForkedFrom = ForkedFrom,
            ForkedAt = ForkedAt,
            Instructions = Instructions,
            McpServers = McpServers
        };
}
