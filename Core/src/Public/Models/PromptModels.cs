namespace Markwardt.LiveAi;

/// <summary>
/// Which model runs each <see cref="PromptCapability" />. A capability without a model of its own uses <see cref="Default" />, and when that is not set either, the tool's own choice: for Claude Code and Codex the models they have always used (<c>haiku</c>, <c>sonnet</c> and <c>opus</c>, and <c>gpt-5.6-luna</c>, <c>gpt-5.6-terra</c> and <c>gpt-5.6-sol</c>), for OpenCode the model of its own configuration. The names to use come from an <see cref="IPromptModelCatalog" />.
/// </summary>
public record PromptModels
{
    /// <summary>
    /// Makes the models for one model that runs every capability.
    /// </summary>
    /// <param name="model">The name of the model, as the tool takes it.</param>
    /// <returns>The models.</returns>
    /// <exception cref="ArgumentException"><paramref name="model" /> is empty.</exception>
    public static PromptModels Only(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new PromptModels { Default = model };
    }

    /// <summary>
    /// Makes the models for a choice of model for each capability that is given; a capability that is not given keeps the tool's own choice.
    /// </summary>
    /// <param name="models">The model of each capability, each capability at most once.</param>
    /// <returns>The models.</returns>
    /// <exception cref="ArgumentException">A name is empty, or a capability is given twice.</exception>
    public static PromptModels ByCapability(IEnumerable<(PromptCapability Capability, string Model)> models)
    {
        Dictionary<PromptCapability, string> chosen = Choose(models);
        return new() { Fast = chosen.GetValueOrDefault(PromptCapability.Fast), Balanced = chosen.GetValueOrDefault(PromptCapability.Balanced), Max = chosen.GetValueOrDefault(PromptCapability.Max) };
    }

    /// <summary>
    /// Collects the model of each capability, refusing an empty name and a capability that is given twice.
    /// </summary>
    /// <param name="models">The model of each capability.</param>
    /// <returns>The models by capability.</returns>
    /// <exception cref="ArgumentException">A name is empty, or a capability is given twice.</exception>
    protected static Dictionary<PromptCapability, string> Choose(IEnumerable<(PromptCapability Capability, string Model)> models)
    {
        Dictionary<PromptCapability, string> chosen = [];

        foreach ((PromptCapability capability, string model) in models)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model, nameof(models));

            if (!chosen.TryAdd(capability, model))
            {
                throw new ArgumentException($"The capability {capability} is given more than once.", nameof(models));
            }
        }

        return chosen;
    }

    /// <summary>
    /// Gets the model used for every capability that has no model of its own, or <see langword="null" /> to leave those to the tool.
    /// </summary>
    public string? Default { get; init; }

    /// <summary>
    /// Gets the model used for <see cref="PromptCapability.Fast" />, or <see langword="null" /> to use <see cref="Default" />.
    /// </summary>
    public string? Fast { get; init; }

    /// <summary>
    /// Gets the model used for <see cref="PromptCapability.Balanced" />, or <see langword="null" /> to use <see cref="Default" />.
    /// </summary>
    public string? Balanced { get; init; }

    /// <summary>
    /// Gets the model used for <see cref="PromptCapability.Max" />, or <see langword="null" /> to use <see cref="Default" />.
    /// </summary>
    public string? Max { get; init; }

    /// <summary>
    /// Finds the model for a capability.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <returns>The name of the model of the capability, or <see langword="null" /> when the tool's own choice applies.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability" /> is not a defined capability.</exception>
    public string? For(PromptCapability capability)
        => capability switch
        {
            PromptCapability.Fast => Fast ?? Default,
            PromptCapability.Balanced => Balanced ?? Default,
            PromptCapability.Max => Max ?? Default,
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
}
