namespace Markwardt.LiveAi;

/// <summary>
/// A setting that one model offers, such as the reasoning effort it runs at. A model lists the settings it has, so an application can show them without knowing any tool; the value chosen is kept with the session record under <see cref="Id" />. Values are always written as text.
/// </summary>
internal sealed record PromptModelSetting
{
    /// <summary>
    /// Makes a setting whose value is one of a list.
    /// </summary>
    /// <param name="id">The identifier of the setting.</param>
    /// <param name="name">The name to show.</param>
    /// <param name="choices">The values, ordered from the lowest to the highest.</param>
    /// <param name="defaultChoice">The value used until another is chosen, or <see langword="null" /> for <c>medium</c> when it is one of the choices and otherwise the first.</param>
    /// <returns>The setting.</returns>
    /// <exception cref="ArgumentException"><paramref name="choices" /> is empty, or <paramref name="defaultChoice" /> is not one of them.</exception>
    public static PromptModelSetting Choose(string id, string name, IEnumerable<string> choices, string? defaultChoice = null)
    {
        string[] values = [.. choices];

        if (values.Length == 0)
        {
            throw new ArgumentException("A choice setting needs at least one choice.", nameof(choices));
        }

        string fallback = defaultChoice ?? (values.Contains("medium") ? "medium" : values[0]);

        if (!values.Contains(fallback))
        {
            throw new ArgumentException($"The default '{fallback}' is not one of the choices.", nameof(defaultChoice));
        }

        return new() { Id = id, Name = name, Kind = PromptModelSettingKind.Choice, Choices = values, Default = fallback };
    }

    /// <summary>
    /// Makes a setting that is on or off.
    /// </summary>
    /// <param name="id">The identifier of the setting.</param>
    /// <param name="name">The name to show.</param>
    /// <param name="defaultValue">Whether it is on until another is chosen.</param>
    /// <returns>The setting.</returns>
    public static PromptModelSetting Switch(string id, string name, bool defaultValue = false)
        => new() { Id = id, Name = name, Kind = PromptModelSettingKind.Toggle, Default = defaultValue ? "true" : "false" };

    /// <summary>
    /// Makes a setting whose value is a whole number.
    /// </summary>
    /// <param name="id">The identifier of the setting.</param>
    /// <param name="name">The name to show.</param>
    /// <param name="minimum">The smallest value.</param>
    /// <param name="maximum">The largest value.</param>
    /// <param name="defaultValue">The value used until another is chosen.</param>
    /// <returns>The setting.</returns>
    /// <exception cref="ArgumentException">The range is empty, or the default is outside it.</exception>
    public static PromptModelSetting Count(string id, string name, int minimum, int maximum, int defaultValue)
    {
        if (minimum > maximum || defaultValue < minimum || defaultValue > maximum)
        {
            throw new ArgumentException("The range is empty or the default is outside it.");
        }

        return new() { Id = id, Name = name, Kind = PromptModelSettingKind.Number, Minimum = minimum, Maximum = maximum, Default = defaultValue.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>
    /// Gets the identifier of the setting, which is the key of its value in the settings of the model.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name to show for the setting.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets what the setting does, or <see langword="null" /> when there is nothing to add to its name.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets what kind of value the setting takes.
    /// </summary>
    public required PromptModelSettingKind Kind { get; init; }

    /// <summary>
    /// Gets the values of a <see cref="PromptModelSettingKind.Choice" /> setting, ordered from the lowest to the highest, and none for the other kinds.
    /// </summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// Gets the smallest value of a <see cref="PromptModelSettingKind.Number" /> setting.
    /// </summary>
    public int? Minimum { get; init; }

    /// <summary>
    /// Gets the largest value of a <see cref="PromptModelSettingKind.Number" /> setting.
    /// </summary>
    public int? Maximum { get; init; }

    /// <summary>
    /// Gets the value used until another is chosen.
    /// </summary>
    public required string Default { get; init; }

    /// <summary>
    /// Tells whether a value is one this setting takes.
    /// </summary>
    /// <param name="value">The value, as text.</param>
    /// <returns>Whether the value is a choice of the setting, <c>true</c> or <c>false</c> for a toggle, or a whole number in range.</returns>
    public bool Accepts(string value)
        => Kind switch
        {
            PromptModelSettingKind.Choice => Choices.Contains(value),
            PromptModelSettingKind.Toggle => value is "true" or "false",
            PromptModelSettingKind.Number => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= (Minimum ?? int.MinValue) && number <= (Maximum ?? int.MaxValue),
            _ => false
        };
}
