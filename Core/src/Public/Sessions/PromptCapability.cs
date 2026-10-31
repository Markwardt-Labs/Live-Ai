namespace Markwardt.LiveAi;

/// <summary>
/// How capable a session's model is, which each <see cref="IPromptEngine" /> translates to one of its own models.
/// </summary>
public enum PromptCapability
{
    /// <summary>
    /// The fastest and cheapest model.
    /// </summary>
    Fast,

    /// <summary>
    /// The everyday model, balancing speed and ability.
    /// </summary>
    Balanced,

    /// <summary>
    /// The most capable model.
    /// </summary>
    Max
}
