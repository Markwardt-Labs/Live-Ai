namespace Markwardt.LiveAi;

/// <summary>
/// Works out the values of a model's settings: which stored ones still apply, and which a test starts with.
/// </summary>
internal static class PromptModelSettings
{
    extension(IReadOnlyList<PromptModel> models)
    {
        /// <summary>
        /// Picks the model that fits a capability among the ones that can call tools: the one a name asks for when there is one, otherwise the one whose name says which tier it is (small, everyday, strong or top) that is nearest to the capability, the higher one when two are as near, and when no name says, by position in the list the tool gave.
        /// </summary>
        /// <param name="capability">How capable the model should be.</param>
        /// <param name="preferred">The name of the model the caller asked for, or <see langword="null" /> to pick by capability.</param>
        /// <returns>The model.</returns>
        /// <exception cref="PromptSessionException">The tool offers no usable model, or not the one asked for.</exception>
        public PromptModel ChooseFor(PromptCapability capability, string? preferred = null)
        {
            List<PromptModel> usable = [.. models.Where(model => model.CallsTools != false)];

            if (usable.Count == 0)
            {
                throw new PromptSessionException("The tool offers no model that can use tools.");
            }

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return usable.FirstOrDefault(model => string.Equals(model.Id, preferred, StringComparison.OrdinalIgnoreCase) || string.Equals(model.Name, preferred, StringComparison.OrdinalIgnoreCase) || model.Id.EndsWith("/" + preferred, StringComparison.OrdinalIgnoreCase)) ?? throw new PromptSessionException($"The tool does not offer a model '{preferred}'.");
            }

            string[][] tierHints =
            [
                ["haiku", "mini", "luna", "flash", "lite", "nano", "small"],
                ["sonnet", "terra", "balanced"],
                ["opus"],
                ["fable", "sol", "max", "pro", "large", "ultra"]
            ];
            int wanted = (int)capability;
            (PromptModel Model, int Tier)[] ranked = [.. usable.Select(model => (Model: model, Tier: tierHints.Select((hints, tier) => hints.Any(hint => model.Id.Contains(hint, StringComparison.OrdinalIgnoreCase)) ? tier : -1).FirstOrDefault(tier => tier >= 0, -1))).Where(entry => entry.Tier >= 0)];

            if (ranked.Length > 0)
            {
                return ranked.OrderBy(entry => Math.Abs(entry.Tier - wanted)).ThenByDescending(entry => entry.Tier).First().Model;
            }

            return usable[(int)Math.Round((double)wanted / (Enum.GetValues<PromptCapability>().Length - 1) * (usable.Count - 1))];
        }
    }

    extension(PromptModel model)
    {
        /// <summary>
        /// Makes the settings a model runs with for an effort: the effort setting at the level nearest to it among the levels the model offers, the caller's overrides where the model accepts them, and the defaults for everything else.
        /// </summary>
        /// <param name="effort">How much effort the model should put into each prompt.</param>
        /// <param name="overrides">Values the caller asked for, or <see langword="null" /> for none.</param>
        /// <returns>The settings.</returns>
        public Dictionary<string, string> SettingsFor(PromptEffort effort, IReadOnlyDictionary<string, string>? overrides = null)
        {
            Dictionary<string, string> wanted = new(overrides ?? new Dictionary<string, string>(), StringComparer.Ordinal);

            if (model.Settings?.FirstOrDefault(setting => setting.Id is "effort" or "variant" && setting.Kind == PromptModelSettingKind.Choice && setting.Choices.Count > 0) is { } level)
            {
                string name = effort.ToString();
                wanted[level.Id] = level.Choices.FirstOrDefault(choice => string.Equals(choice, name, StringComparison.OrdinalIgnoreCase)) ?? (effort == PromptEffort.Max ? level.Choices[^1] : level.Choices[(int)Math.Round((double)(int)effort / (Enum.GetValues<PromptEffort>().Length - 1) * (level.Choices.Count - 1))]);
            }

            return model.Reconcile(wanted).Settings;
        }

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
