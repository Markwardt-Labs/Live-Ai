namespace Markwardt.LiveAi;

/// <summary>
/// How much reasoning effort a session's model puts into each prompt.
/// </summary>
public enum PromptReasoning
{
    /// <summary>
    /// The least reasoning.
    /// </summary>
    Low,

    /// <summary>
    /// Moderate reasoning.
    /// </summary>
    Medium,

    /// <summary>
    /// Deep reasoning.
    /// </summary>
    High,

    /// <summary>
    /// The most reasoning.
    /// </summary>
    Max
}
