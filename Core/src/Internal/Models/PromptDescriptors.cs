namespace Markwardt.LiveAi;

/// <summary>
/// Makes the descriptors of a session: what it knows about how it is run, written for a person to read, with names such as <c>Model</c> and <c>Context window</c> and values such as <c>High</c>, <c>On</c> and <c>16,384</c>.
/// </summary>
internal static class PromptDescriptors
{
    /// <summary>
    /// Describes a tool and, when one was chosen, its model with the settings it runs with.
    /// </summary>
    /// <param name="provider">The name of the tool.</param>
    /// <param name="model">The model that runs, or <see langword="null" /> before one was chosen.</param>
    /// <param name="settings">The values of the settings of the model, by setting identifier.</param>
    /// <returns>The descriptors, in the order they are best read.</returns>
    public static IReadOnlyDictionary<string, string> Describe(string provider, PromptModel? model, IReadOnlyDictionary<string, string> settings)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal) { ["Provider"] = provider };

        if (model is null)
        {
            return found;
        }

        string name = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name;
        found["Model"] = name;

        if (name != model.Id)
        {
            found["Model ID"] = model.Id;
        }

        foreach ((string id, string value) in settings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            PromptModelSetting? setting = model.Settings?.FirstOrDefault(candidate => candidate.Id == id);
            found[Unique(found, setting?.Name ?? Words(id))] = Value(setting, value);
        }

        return found;
    }

    /// <summary>
    /// Writes a name for reading: the first letter in upper case.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with its first letter in upper case.</returns>
    public static string Capitalized(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Unique(Dictionary<string, string> found, string name)
        => found.ContainsKey(name) ? $"{name} setting" : name;

    private static string Words(string id)
        => Capitalized(Regex.Replace(id.Replace('_', ' ').Replace('-', ' ').Replace('.', ' '), "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant());

    private static string Value(PromptModelSetting? setting, string value)
        => setting?.Kind switch
        {
            PromptModelSettingKind.Toggle => value == "true" ? "On" : "Off",
            PromptModelSettingKind.Number when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) => number.ToString("N0", CultureInfo.InvariantCulture),
            _ => value is "true" or "false" ? (value == "true" ? "On" : "Off") : Capitalized(value)
        };
}
