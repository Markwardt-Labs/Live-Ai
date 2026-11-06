namespace Markwardt.LiveAi;

/// <summary>
/// The size of the context window to run a model with for each <see cref="PromptCapability" />, for a service where the caller sets it. A capability without a size runs its model with the size the service picks. Use <see cref="All" /> for one size whatever the capability is.
/// </summary>
public sealed record PromptCapabilityContexts
{
    /// <summary>
    /// Makes the sizes of a session that runs every capability with the same size.
    /// </summary>
    /// <param name="tokens">The size of the context window, in tokens.</param>
    /// <returns>The sizes.</returns>
    public static PromptCapabilityContexts All(int tokens)
        => new() { Fast = tokens, Balanced = tokens, Strong = tokens, Max = tokens };

    /// <summary>
    /// Gets the size of the context window, in tokens, for <see cref="PromptCapability.Fast" />, or <see langword="null" /> for the size the service picks.
    /// </summary>
    public int? Fast { get; init; }

    /// <summary>
    /// Gets the size of the context window, in tokens, for <see cref="PromptCapability.Balanced" />, or <see langword="null" /> for the size the service picks.
    /// </summary>
    public int? Balanced { get; init; }

    /// <summary>
    /// Gets the size of the context window, in tokens, for <see cref="PromptCapability.Strong" />, or <see langword="null" /> for the size the service picks.
    /// </summary>
    public int? Strong { get; init; }

    /// <summary>
    /// Gets the size of the context window, in tokens, for <see cref="PromptCapability.Max" />, or <see langword="null" /> for the size the service picks.
    /// </summary>
    public int? Max { get; init; }

    /// <summary>
    /// Gets the size of the context window for a capability.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <returns>The size in tokens, or <see langword="null" /> for the size the service picks.</returns>
    public int? For(PromptCapability capability)
        => capability switch
        {
            PromptCapability.Fast => Fast,
            PromptCapability.Balanced => Balanced,
            PromptCapability.Strong => Strong,
            _ => Max
        };
}
