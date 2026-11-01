namespace Markwardt.LiveAi;

/// <summary>
/// A model that a tool can run, as an <see cref="IPromptModelCatalog" /> lists it.
/// </summary>
public sealed record PromptModel
{
    /// <summary>
    /// Gets the name the tool takes for the model, which is what <see cref="PromptModels" /> is given.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name to show for the model, or <see langword="null" /> to use <see cref="Id" />.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets how many tokens the model's context window holds, or <see langword="null" /> when the catalog cannot tell. For a local model it is the size the server runs the model with, which it only reports while the model is loaded and which is often smaller than the model's maximum.
    /// </summary>
    public int? ContextLimit { get; init; }

    /// <summary>
    /// Gets the reasoning efforts the model accepts as the tool names them, lowest first, or <see langword="null" /> when the catalog cannot tell.
    /// </summary>
    public IReadOnlyList<string>? Efforts { get; init; }
}
