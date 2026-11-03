namespace Markwardt.LiveAi;

/// <summary>
/// What kind of value a <see cref="PromptModelSetting" /> takes.
/// </summary>
public enum PromptModelSettingKind
{
    /// <summary>
    /// One of a list of values, ordered from the lowest to the highest (a reasoning effort, say).
    /// </summary>
    Choice,

    /// <summary>
    /// On or off, written <c>true</c> or <c>false</c>.
    /// </summary>
    Toggle,

    /// <summary>
    /// A whole number, within a range.
    /// </summary>
    Number
}
