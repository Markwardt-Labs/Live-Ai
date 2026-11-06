namespace Markwardt.LiveAi;

/// <summary>
/// A model that an <see cref="OpenCodeModelProvider" /> serves.
/// </summary>
internal sealed record OpenCodeModel
{
    /// <summary>
    /// Gets the identifier the provider's API knows the model by, such as <c>gemma4:26b</c> for Ollama.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name to show for the model, or <see langword="null" /> to use <see cref="Id" />.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets how many tokens the model's context window holds, or <see langword="null" /> when OpenCode should assume its default (32768 when only <see cref="OutputLimit" /> is given). OpenCode uses it to track how full the context is and to compact it, so give the size the server actually runs the model with, which for a local server is often smaller than the model's maximum.
    /// </summary>
    public int? ContextLimit { get; init; }

    /// <summary>
    /// Gets the most tokens the model may write in one answer, or <see langword="null" /> for the smaller of 8192 and half the context window. OpenCode drops a model whose limits are incomplete, so the other limit is always filled in.
    /// </summary>
    public int? OutputLimit { get; init; }
}
