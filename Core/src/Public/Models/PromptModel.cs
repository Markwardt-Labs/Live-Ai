namespace Markwardt.LiveAi;

/// <summary>
/// A model that a tool can run, as <see cref="IPromptProvider.GetModels" /> lists it: what to call it, what it can do, and the settings it offers. A session runs a model once it is chosen with <see cref="IPromptSessionConfiguration.Model" />.
/// </summary>
public sealed record PromptModel
{
    /// <summary>
    /// Gets the name the tool takes for the model, and the key a stored session finds it by.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name to show for the model, or <see langword="null" /> to use <see cref="Id" />.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets what the tool says about the model, or <see langword="null" /> when it says nothing.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets whether the model takes images as input, or <see langword="null" /> when the tool does not say.
    /// </summary>
    public bool? AcceptsImages { get; init; }

    /// <summary>
    /// Gets whether the model can call tools, or <see langword="null" /> when the tool does not say. A model that cannot is of no use to a session.
    /// </summary>
    public bool? CallsTools { get; init; }

    /// <summary>
    /// Gets how many tokens the context window holds, or <see langword="null" /> when it is not known or the tool manages it. For a model on this machine it is the size the server runs the model with, which the server only reports while the model is loaded.
    /// </summary>
    public int? ContextLimit { get; init; }

    /// <summary>
    /// Gets the most tokens the model can take in its context window, or <see langword="null" /> when it is not known.
    /// </summary>
    public int? MaxContextLimit { get; init; }

    /// <summary>
    /// Gets the settings the model offers, none for a model that has none, or <see langword="null" /> when they are not known: a model a stored session names that the tool could not be asked about. The values chosen are passed to the tool as they are.
    /// </summary>
    public IReadOnlyList<PromptModelSetting>? Settings { get; init; }
}
