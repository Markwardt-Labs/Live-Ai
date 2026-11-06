namespace Markwardt.LiveAi;

/// <summary>
/// How much effort a generator puts into an image, which each generator turns into its own settings (a quality level for a service, a number of steps for a local model).
/// </summary>
internal enum PromptImageQuality
{
    /// <summary>
    /// The fastest and cheapest, for a first look.
    /// </summary>
    Draft,

    /// <summary>
    /// The everyday setting.
    /// </summary>
    Standard,

    /// <summary>
    /// The most detail, which takes the longest and costs the most.
    /// </summary>
    High
}
