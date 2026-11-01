namespace Markwardt.LiveAi;

/// <summary>
/// Lists the models of Claude Code. The tool has no command that lists models, so these are the aliases it takes: each stands for the newest model of its tier, so choosing one never goes stale. The full name of a model can be given to <see cref="PromptModels" /> as well.
/// </summary>
public sealed class ClaudeModelCatalog : IPromptModelCatalog
{
    private readonly IReadOnlyList<PromptModel> models =
    [
        new() { Id = "haiku", Name = "Claude Haiku (newest)" },
        new() { Id = "sonnet", Name = "Claude Sonnet (newest)" },
        new() { Id = "opus", Name = "Claude Opus (newest)" }
    ];

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptModel>> List(CancellationToken cancellation = default)
        => Task.FromResult(models);
}
