namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSetupJsonTests
{
    [Fact]
    public void Claude_KeepsItsKindAndTheKeyButNotTheWorkspaceNorTheStartingConfiguration()
    {
        PromptSetup options = new ClaudeSetup { ApiKey = "secret", WorkspacePath = "/project", Capability = PromptCapability.Max, Instructions = "Be brief.", IsImageGenerationAllowed = true };

        string json = JsonSerializer.Serialize(options);
        PromptSetup? back = JsonSerializer.Deserialize<PromptSetup>(json);

        Assert.DoesNotContain("/project", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Be brief.", json, StringComparison.Ordinal);
        Assert.Equal(new ClaudeSetup { ApiKey = "secret" }, back);
    }

    [Fact]
    public void Codex_KeepsItsKindAndTheKeyAndNothingOfTheEnvironment()
    {
        PromptSetup options = new CodexSetup { ApiKey = "secret" };

        string json = JsonSerializer.Serialize(options);

        Assert.Equal(options, JsonSerializer.Deserialize<PromptSetup>(json));
        Assert.DoesNotContain("ommand", json, StringComparison.Ordinal);
        Assert.DoesNotContain("onfig", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_KeepsTheOverridesOfTheServerTheModelsAndTheContextOfEachCapability()
    {
        PromptSetup options = new LocalSetup { Overrides = new PromptServerSettings { Server = new Uri("http://box:11434/v1"), Models = new PromptCapabilityModels { Fast = "small", Balanced = "gemma4:26b", Strong = "big", Max = "huge" }, Contexts = new PromptCapabilityContexts { Fast = 4096, Max = 65536 } } };

        PromptSetup? back = JsonSerializer.Deserialize<PromptSetup>(JsonSerializer.Serialize(options));

        Assert.Equal(options, back);
    }

    [Fact]
    public void Local_WithoutOverrides_ComesBackWithNone()
    {
        PromptSetup options = new LocalSetup();

        PromptSetup? back = JsonSerializer.Deserialize<PromptSetup>(JsonSerializer.Serialize(options));

        Assert.Equal(new PromptServerSettings(), Assert.IsType<LocalSetup>(back).Overrides);
    }

    [Fact]
    public void ThePolymorphicSetupOfALinkComeBackAsTheSameKind()
    {
        PromptSessionLink link = new() { Id = Guid.NewGuid(), Provider = "Codex", WorkingFolder = "/w", Setup = new CodexSetup(), IsImageGenerationAllowed = true, Capability = PromptCapability.Strong, Effort = PromptEffort.High, IsWebAllowed = true, Instructions = "Be brief." };

        string json = JsonSerializer.Serialize(link);
        PromptSessionLink? back = JsonSerializer.Deserialize<PromptSessionLink>(json);

        Assert.Equal(link, back);
    }
}
