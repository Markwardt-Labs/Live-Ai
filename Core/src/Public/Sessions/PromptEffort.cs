namespace Markwardt.LiveAi;

/// <summary>
/// How much effort the model of a session puts into each prompt. The library maps it to the levels the chosen model offers, so a model with fewer levels gets the nearest one.
/// </summary>
public enum PromptEffort
{
    /// <summary>
    /// The least effort.
    /// </summary>
    Low,

    /// <summary>
    /// Moderate effort.
    /// </summary>
    Medium,

    /// <summary>
    /// Deep effort.
    /// </summary>
    High,

    /// <summary>
    /// The most effort.
    /// </summary>
    Max
}
