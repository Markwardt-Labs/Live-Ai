namespace Markwardt.LiveAi.Tests.Unit.Models;

public sealed class ClaudeModelCatalogTests
{
    [Fact]
    public async Task List_GivesTheAliasesOfTheThreeTiers()
        => Assert.Equal(["haiku", "sonnet", "opus"], (await new ClaudeModelCatalog().List()).Select(model => model.Id));
}
