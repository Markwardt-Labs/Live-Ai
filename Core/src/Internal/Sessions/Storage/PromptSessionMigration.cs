namespace Markwardt.LiveAi;

/// <summary>
/// Upgrades the stored files of sessions from the layout an earlier version wrote to the current one, so that a session stored by any version can be loaded by this one.
/// </summary>
internal interface IPromptSessionMigration
{
    /// <summary>
    /// Gets the version of the layout this version of the library writes.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Upgrades the content of a stored file in place, one version after the other, until it has the current layout. A file that already has it is not touched.
    /// </summary>
    /// <param name="link">The content of the file as JSON.</param>
    /// <returns>The version the file had when it needed an upgrade, or <see langword="null" /> when it had the current one or could not be upgraded because its tool is not known (it is left as it was).</returns>
    /// <exception cref="PromptSessionException">A newer version of the library wrote the file, or it names a layout that does not exist.</exception>
    int? Upgrade(JsonObject link);
}

/// <inheritdoc />
internal sealed class PromptSessionMigration : IPromptSessionMigration
{
    private readonly List<Func<JsonObject, bool>> steps = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptSessionMigration" /> class.
    /// </summary>
    public PromptSessionMigration()
    {
        steps.Add(UpgradeToTwo);
        steps.Add(UpgradeToThree);
        steps.Add(UpgradeToFour);
        steps.Add(UpgradeToFive);
    }

    /// <inheritdoc />
    public int Version => 1 + steps.Count;

    /// <inheritdoc />
    public int? Upgrade(JsonObject link)
    {
        int found = VersionOf(link);

        if (found < 1)
        {
            throw new PromptSessionException($"The stored session has the layout {found}, which does not exist.");
        }

        if (found > Version)
        {
            throw new PromptSessionException($"The stored session was written by a newer version of the library (layout {found}, this one reads up to {Version}).");
        }

        for (int step = found - 1; step < steps.Count; step++)
        {
            if (!steps[step](link))
            {
                return null;
            }
        }

        if (found == Version)
        {
            return null;
        }

        link.Remove("version");
        link.Insert(0, "version", Version);
        return found;
    }

    private int VersionOf(JsonObject link)
        => link["version"] is JsonValue value && value.TryGetValue(out int number) ? number : link.ContainsKey("options") ? 2 : 1;

    private string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private bool UpgradeToFive(JsonObject link)
    {
        int index = link.IndexOf("isSandboxed");

        if (index >= 0)
        {
            JsonNode? sandboxed = link["isSandboxed"];
            link.RemoveAt(index);

            if (sandboxed is JsonValue value && value.TryGetValue(out bool confined))
            {
                link.Insert(index, "isSystemAccessAllowed", !confined);
            }
        }

        return true;
    }

    private bool UpgradeToFour(JsonObject link)
    {
        int index = link.IndexOf("created");

        if (index >= 0)
        {
            JsonNode? created = link["created"];
            link.RemoveAt(index);

            if (created is not null && !link.ContainsKey("lastLoaded"))
            {
                link.Insert(index, "lastLoaded", created);
            }
        }

        return true;
    }

    private bool UpgradeToThree(JsonObject link)
    {
        string? tool = Text((link["options"] as JsonObject)?["tool"]);

        string? provider = Text(link["provider"]) switch
        {
            "Claude Code" => "claude",
            "Codex" => "codex",
            "OpenCode" => "local",
            _ => tool is "claude" or "codex" or "local" ? tool : null
        };

        if (provider is null)
        {
            return false;
        }

        link["provider"] = provider;
        return true;
    }

    private bool UpgradeToTwo(JsonObject link)
    {
        string? tool = Text(link["provider"]) switch
        {
            "Claude Code" => "claude",
            "Codex" => "codex",
            "OpenCode" => "local",
            _ => null
        };

        if (tool is null)
        {
            return false;
        }

        link["options"] = new JsonObject { ["tool"] = tool };

        if (link["sandbox"] is JsonNode sandbox)
        {
            link["isSandboxed"] ??= sandbox.DeepClone();
            link.Remove("sandbox");
        }

        if (Text(link["model"]) is { } model && model.CapabilityOf() is { } capability)
        {
            link["capability"] ??= capability.ToString();
        }

        JsonObject? settings = link["modelSettings"] as JsonObject;

        if ((Text(settings?["effort"]) ?? Text(settings?["variant"]))?.ToLowerInvariant() switch { "low" => PromptEffort.Low, "medium" => PromptEffort.Medium, "high" => PromptEffort.High, "xhigh" or "max" => PromptEffort.Max, _ => null as PromptEffort? } is { } effort)
        {
            link["effort"] ??= effort.ToString();
        }

        link.Remove("model");
        link.Remove("modelSettings");
        return true;
    }
}
