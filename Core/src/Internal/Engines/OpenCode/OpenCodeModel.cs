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
    /// Gets how many tokens the model's context window holds, or <see langword="null" /> when it is not known (OpenCode then never compacts the conversation to make room). OpenCode uses it to track how full the context is and to compact it, so give the size the server actually runs the model with, which for a local server is often smaller than the model's maximum.
    /// </summary>
    public int? ContextLimit { get; init; }

    /// <summary>
    /// Gets the most tokens the model may write in one answer, or <see langword="null" /> for as many as OpenCode allows: it reduces any limit to what the context window leaves after the prompt and to 256000, so a model that reasons for long is never cut off for want of a limit that was too small. OpenCode drops a model whose limits are incomplete, so both are always written.
    /// </summary>
    public int? OutputLimit { get; init; }

    /// <summary>
    /// Gets the identifier to send to the provider's API instead of <see cref="Id" />, or <see langword="null" /> to send <see cref="Id" />. OpenCode keeps calling the model by <see cref="Id" />.
    /// </summary>
    public string? ApiId { get; init; }
}
