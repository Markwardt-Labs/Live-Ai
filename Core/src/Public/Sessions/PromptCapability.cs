namespace Markwardt.LiveAi;

/// <summary>
/// How capable the model of a session is. The library picks the model of the tool that fits, in the background, so a caller never names one. A tool that has fewer tiers than there are levels gives a level the nearest tier it has, the higher one when two are as near.
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
    /// A model better than the everyday one, short of the most capable (for Claude Code the step between Sonnet and the top model).
    /// </summary>
    Strong,

    /// <summary>
    /// The most capable model.
    /// </summary>
    Max
}
