namespace Markwardt.LiveAi;

/// <summary>
/// An image model that a service can make images with, as the catalog of a service lists it: what to call it and what it can do beyond the basics.
/// </summary>
internal sealed record PromptImageModel
{
    /// <summary>
    /// Gets the name the service takes for the model, which the factory of a generator takes as its model (for Stable Diffusion the title of the checkpoint).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name to show for the model, or <see langword="null" /> to use <see cref="Id" />.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets what the generator made with this model can do beyond a prompt, a size and a quality, which is what the AI is offered.
    /// </summary>
    public required PromptImageFeatures Features { get; init; }

    /// <summary>
    /// Gets when the service published the model, or <see langword="null" /> when it does not say.
    /// </summary>
    public DateTimeOffset? Created { get; init; }
}
