namespace Markwardt.LiveAi;

/// <summary>
/// The model that runs for each <see cref="PromptCapability" />, for a service where the caller names the models itself (a local server, say) instead of leaving the choice to the library: the model the caller chose for how capable the session should be. Use <see cref="All" /> to run one model whatever the capability is.
/// </summary>
public sealed record PromptCapabilityModels
{
    /// <summary>
    /// Makes the models of a session that runs one model whatever its capability is.
    /// </summary>
    /// <param name="model">The name of the model, as the service lists it.</param>
    /// <returns>The models.</returns>
    public static PromptCapabilityModels All(string model)
        => new() { Fast = model, Balanced = model, Strong = model, Max = model };

    /// <summary>
    /// Gets the name of the model that runs for <see cref="PromptCapability.Fast" />, as the service lists it (<c>gemma4:26b</c>).
    /// </summary>
    public required string Fast { get; init; }

    /// <summary>
    /// Gets the name of the model that runs for <see cref="PromptCapability.Balanced" />.
    /// </summary>
    public required string Balanced { get; init; }

    /// <summary>
    /// Gets the name of the model that runs for <see cref="PromptCapability.Strong" />.
    /// </summary>
    public required string Strong { get; init; }

    /// <summary>
    /// Gets the name of the model that runs for <see cref="PromptCapability.Max" />.
    /// </summary>
    public required string Max { get; init; }

    /// <summary>
    /// Gets the name of the model for a capability.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <returns>The name of the model.</returns>
    public string For(PromptCapability capability)
        => capability switch
        {
            PromptCapability.Fast => Fast,
            PromptCapability.Balanced => Balanced,
            PromptCapability.Strong => Strong,
            _ => Max
        };
}
