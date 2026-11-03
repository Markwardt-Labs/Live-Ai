namespace Markwardt.LiveAi;

/// <summary>
/// Works out the values of a model's settings: which stored ones still apply, and which a test starts with.
/// </summary>
internal static class PromptModelSettings
{
    extension(PromptModel model)
    {
        /// <summary>
        /// Brings the settings a session was stored with up to date with what the model offers now: settings the model no longer has are dropped, values it no longer accepts go back to the setting's default, and settings the stored session lacks get theirs. A model whose settings are not known keeps the stored ones as they are.
        /// </summary>
        /// <param name="stored">The settings as they were stored.</param>
        /// <returns>The settings to use, and a note for everything that changed.</returns>
        public (Dictionary<string, string> Settings, IReadOnlyList<string> Notes) Reconcile(IReadOnlyDictionary<string, string> stored)
        {
            if (model.Settings is null)
            {
                return (new Dictionary<string, string>(stored, StringComparer.Ordinal), []);
            }

            Dictionary<string, string> result = new(StringComparer.Ordinal);
            List<string> notes = [];

            foreach (PromptModelSetting setting in model.Settings)
            {
                if (!stored.TryGetValue(setting.Id, out string? value))
                {
                    result[setting.Id] = setting.Default;
                }
                else if (setting.Accepts(value))
                {
                    result[setting.Id] = value;
                }
                else
                {
                    result[setting.Id] = setting.Default;
                    notes.Add($"The value '{value}' of the setting '{setting.Id}' is no longer accepted by {model.Id}; it is back to '{setting.Default}'.");
                }
            }

            notes.AddRange(stored.Keys.Where(id => !result.ContainsKey(id)).Select(id => $"The setting '{id}' is no longer offered by {model.Id} and was dropped."));
            return (result, notes);
        }

        /// <summary>
        /// Gives the settings a test of the tool runs with: the lowest choice of every choice setting, and the default of the others.
        /// </summary>
        /// <returns>The value of each setting.</returns>
        public Dictionary<string, string> Lowest()
            => new((model.Settings ?? []).Select(setting => KeyValuePair.Create(setting.Id, setting.Kind == PromptModelSettingKind.Choice ? setting.Choices[0] : setting.Default)), StringComparer.Ordinal);
    }
}
