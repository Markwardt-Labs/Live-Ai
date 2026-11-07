namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionMigrationTests
{
    private readonly PromptSessionMigration migration = new();

    private JsonObject Old(string provider = "Claude Code", string? model = "sonnet", string? effort = "high", bool? sandbox = false)
    {
        JsonObject link = new()
        {
            ["id"] = "4fa79d813fb546d2b3a798e85888d070",
            ["created"] = "2026-11-03T17:25:02.4401399+00:00",
            ["provider"] = provider,
            ["workingFolder"] = "/work",
            ["aiSessionId"] = "dbf4f02b-87d7-43fc-9b45-866c5326f213",
            ["workspacePath"] = "/work",
            ["forkedFrom"] = null,
            ["forkedAt"] = null
        };

        if (sandbox is { } value)
        {
            link["sandbox"] = value;
        }

        if (model is not null)
        {
            link["model"] = model;
        }

        if (effort is not null)
        {
            link["modelSettings"] = new JsonObject { ["effort"] = effort };
        }

        return link;
    }

    [Fact]
    public void Version_IsTheLayoutWithTheSystemAccessSwitch() => Assert.Equal(5, migration.Version);

    [Fact]
    public void Upgrade_TurnsTheFirstLayoutIntoTheCurrentOneAndSaysWhereItCameFrom()
    {
        JsonObject link = Old(sandbox: false);

        int? from = migration.Upgrade(link);

        Assert.Equal(1, from);
        Assert.Equal(5, link["version"]!.GetValue<int>());
        Assert.False(link.ContainsKey("upgradedFrom"));
        Assert.Equal(("2026-11-03T17:25:02.4401399+00:00", false), (link["lastLoaded"]!.GetValue<string>(), link.ContainsKey("created")));
        Assert.Equal("claude", link["options"]!["tool"]!.GetValue<string>());
        Assert.Equal("claude", link["provider"]!.GetValue<string>());
        Assert.True(link["isSystemAccessAllowed"]!.GetValue<bool>());
        Assert.False(link.ContainsKey("isSandboxed"));
        Assert.Equal(("Balanced", "High"), (link["capability"]!.GetValue<string>(), link["effort"]!.GetValue<string>()));
        Assert.All(new[] { "sandbox", "model", "modelSettings" }, name => Assert.False(link.ContainsKey(name)));
        Assert.Equal("dbf4f02b-87d7-43fc-9b45-866c5326f213", link["aiSessionId"]!.GetValue<string>());
        Assert.Equal("/work", link["workspacePath"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Claude Code", "claude")]
    [InlineData("Codex", "codex")]
    [InlineData("OpenCode", "local")]
    public void Upgrade_KnowsTheToolOfEveryProvider(string provider, string tool)
    {
        JsonObject link = Old(provider);

        migration.Upgrade(link);

        Assert.Equal((tool, tool), (link["options"]!["tool"]!.GetValue<string>(), link["provider"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("haiku", "Fast")]
    [InlineData("Luna", "Fast")]
    [InlineData("sonnet", "Balanced")]
    [InlineData("gpt-5.6-terra", "Balanced")]
    [InlineData("opus", "Strong")]
    [InlineData("fable", "Max")]
    [InlineData("gpt-5.6-sol", "Max")]
    public void Upgrade_MapsTheModelToTheCapabilityThatChoosesIt(string model, string capability)
    {
        JsonObject link = Old(model: model);

        migration.Upgrade(link);

        Assert.Equal(capability, link["capability"]!.GetValue<string>());
    }

    [Fact]
    public void Upgrade_TakesTheVariantOfOpenCodeForTheEffortAndPutsTheVersionFirst()
    {
        JsonObject link = Old("OpenCode", model: "ollama/gemma4:26b", effort: null);
        link["modelSettings"] = new JsonObject { ["variant"] = "high", ["context"] = "16384" };

        migration.Upgrade(link);

        Assert.Equal("High", link["effort"]!.GetValue<string>());
        Assert.False(link.ContainsKey("capability"));
        Assert.Equal("version", link.First().Key);
    }

    [Theory]
    [InlineData("low", "Low")]
    [InlineData("MEDIUM", "Medium")]
    [InlineData("high", "High")]
    [InlineData("xhigh", "Max")]
    [InlineData("max", "Max")]
    public void Upgrade_MapsTheEffortSetting(string effort, string expected)
    {
        JsonObject link = Old(effort: effort);

        migration.Upgrade(link);

        Assert.Equal(expected, link["effort"]!.GetValue<string>());
    }

    [Fact]
    public void Upgrade_LeavesTheDefaultsForWhatTheOldLayoutDidNotSayOrTheNameDoesNotTell()
    {
        JsonObject link = Old("OpenCode", model: "ollama/gemma4:26b", effort: null, sandbox: null);

        migration.Upgrade(link);

        Assert.All(new[] { "capability", "effort", "isSystemAccessAllowed", "isShellAllowed", "isWebAllowed", "instructions" }, name => Assert.False(link.ContainsKey(name)));
        Assert.False(link.ContainsKey("model"));
    }

    [Fact]
    public void Upgrade_TurnsALinkOfTheSecondLayoutThatHasNoVersionIntoTheCurrentOne()
    {
        JsonObject link = Old();
        link["options"] = new JsonObject { ["tool"] = "claude" };

        int? from = migration.Upgrade(link);

        Assert.Equal(2, from);
        Assert.Equal(("claude", 5), (link["provider"]!.GetValue<string>(), link["version"]!.GetValue<int>()));
        Assert.Equal("version", link.First().Key);
    }

    [Fact]
    public void Upgrade_RenamesTheTimeOfCreationToTheTimeOfTheLastLoadWhereItWasAndKeepsItsValue()
    {
        JsonObject link = new() { ["version"] = 3, ["id"] = "x", ["created"] = "2026-11-03T17:25:02+00:00", ["provider"] = "codex" };
        JsonObject without = new() { ["version"] = 3, ["id"] = "x", ["provider"] = "codex" };
        JsonObject both = new() { ["version"] = 3, ["created"] = "old", ["lastLoaded"] = "new", ["provider"] = "codex" };

        Assert.Equal(3, migration.Upgrade(link));
        Assert.Equal(3, migration.Upgrade(without));
        Assert.Equal(3, migration.Upgrade(both));

        Assert.Equal(["version", "id", "lastLoaded", "provider"], link.Select(property => property.Key));
        Assert.Equal("2026-11-03T17:25:02+00:00", link["lastLoaded"]!.GetValue<string>());
        Assert.False(without.ContainsKey("lastLoaded"));
        Assert.Equal(("new", false), (both["lastLoaded"]!.GetValue<string>(), both.ContainsKey("created")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Upgrade_TurnsTheSandboxSwitchIntoTheSystemAccessSwitchWhichMeansTheOppositeAndStaysWhereItWas(bool sandboxed, bool allowed)
    {
        JsonObject link = new() { ["version"] = 4, ["id"] = "x", ["isSandboxed"] = sandboxed, ["forkedFrom"] = null, ["provider"] = "codex" };

        Assert.Equal(4, migration.Upgrade(link));

        Assert.Equal(["version", "id", "isSystemAccessAllowed", "forkedFrom", "provider"], link.Select(property => property.Key));
        Assert.Equal(allowed, link["isSystemAccessAllowed"]!.GetValue<bool>());
    }

    [Fact]
    public void Upgrade_LeavesTheFileConfinedWhenTheSandboxSwitchWasMissingOrNotABoolean()
    {
        JsonObject missing = new() { ["version"] = 4, ["provider"] = "codex" };
        JsonObject odd = new() { ["version"] = 4, ["isSandboxed"] = "no", ["provider"] = "codex" };

        migration.Upgrade(missing);
        migration.Upgrade(odd);

        Assert.All(new[] { missing, odd }, link => Assert.Equal((false, false), (link.ContainsKey("isSandboxed"), link.ContainsKey("isSystemAccessAllowed"))));
    }

    [Fact]
    public void Upgrade_TakesTheProviderFromTheSetupWhenTheStoredNameIsNotOneItKnows()
    {
        JsonObject known = new() { ["version"] = 2, ["provider"] = "Something", ["options"] = new JsonObject { ["tool"] = "codex" } };
        JsonObject unknown = new() { ["version"] = 2, ["provider"] = "Something", ["options"] = new JsonObject { ["tool"] = "other" } };
        string before = unknown.ToJsonString();

        Assert.Equal(2, migration.Upgrade(known));
        Assert.Null(migration.Upgrade(unknown));

        Assert.Equal("codex", known["provider"]!.GetValue<string>());
        Assert.Equal(before, unknown.ToJsonString());
    }

    [Fact]
    public void Upgrade_LeavesALinkOfTheCurrentVersionAsItIs()
    {
        JsonObject link = new() { ["version"] = 5, ["provider"] = "codex", ["options"] = new JsonObject { ["tool"] = "codex" } };
        string before = link.ToJsonString();

        Assert.Null(migration.Upgrade(link));
        Assert.Equal(before, link.ToJsonString());
    }

    [Fact]
    public void Upgrade_LeavesALinkOfAnUnknownToolAsItWas()
    {
        JsonObject link = Old("Some Other Tool");
        string before = link.ToJsonString();

        Assert.Null(migration.Upgrade(link));
        Assert.Equal(before, link.ToJsonString());
    }

    [Fact]
    public void Upgrade_TakesFieldsOfTheWrongKindForMissingOnes()
    {
        JsonObject number = Old();
        number["provider"] = 7;
        string before = number.ToJsonString();
        JsonObject odd = Old();
        odd["model"] = 3;
        odd["modelSettings"] = new JsonObject { ["effort"] = new JsonObject() };

        Assert.Null(migration.Upgrade(number));
        Assert.Equal(before, number.ToJsonString());
        Assert.Equal(1, migration.Upgrade(odd));
        Assert.False(odd.ContainsKey("capability"));
        Assert.False(odd.ContainsKey("effort"));
    }

    [Fact]
    public void Upgrade_RefusesALinkANewerVersionWrote()
    {
        JsonObject link = new() { ["version"] = 6, ["provider"] = "codex" };

        PromptSessionException exception = Assert.Throws<PromptSessionException>(() => migration.Upgrade(link));

        Assert.Contains("newer version", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Upgrade_RefusesALayoutThatDoesNotExist(int version)
    {
        JsonObject link = new() { ["version"] = version, ["provider"] = "Codex" };

        Assert.Throws<PromptSessionException>(() => migration.Upgrade(link));
    }
}
