namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptLibraryToolsExtensionsTests
{
    private IEnumerable<string> EveryNameTheLibraryHas()
    {
        IPromptLibraryTools tools = new PromptLibraryTools();

        return typeof(IPromptLibraryTools).GetProperties()
            .Where(property => property.Name.EndsWith("Name", StringComparison.Ordinal) && property.PropertyType == typeof(string))
            .Select(property => (string)property.GetValue(tools)!)
            .Concat(tools.Names);
    }

    private PromptTool Tool(string name)
        => new() { Name = name, Description = "d", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement, Handler = (_, _) => Task.FromResult("ok") };

    [Fact]
    public void GetReservedNames_ListsEveryNameTheLibraryToolsHave()
    {
        IReadOnlyList<string> reserved = new PromptLibraryTools().GetReservedNames();

        Assert.Equal(EveryNameTheLibraryHas().Order(), reserved.Order());
    }

    [Fact]
    public void GetReservedNames_ListsNoNameTwiceAndNotRespond()
    {
        IReadOnlyList<string> reserved = new PromptLibraryTools().GetReservedNames();

        Assert.Equal(reserved.Count, reserved.Distinct().Count());
        Assert.DoesNotContain("respond", reserved);
    }

    [Fact]
    public void TheFakeSessionRefusesEveryNameTheLibraryToolsHaveAndRespond()
    {
        FakePromptSession session = new();

        foreach (string name in EveryNameTheLibraryHas().Append("respond"))
        {
            Assert.Throws<ArgumentException>(() => session.Configuration.Tools.Add(Tool(name)));
        }
    }
}
