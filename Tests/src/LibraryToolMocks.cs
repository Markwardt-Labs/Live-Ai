namespace Markwardt.LiveAi.Tests;

/// <summary>
/// Sets up a mocked <see cref="IPromptLibraryTools" /> with the tools every session is offered.
/// </summary>
internal static class LibraryToolMocks
{
    extension(Mock<IPromptLibraryTools> tools)
    {
        /// <summary>
        /// Names and builds the tools that are offered in every session: the time, the search of tools and the delegation, besides the search of the web.
        /// </summary>
        public void OfferTheEverydayTools()
        {
            tools.SetupGet(mock => mock.SearchName).Returns("search_web");
            tools.SetupGet(mock => mock.GenerateImageName).Returns("generate_image");
            tools.SetupGet(mock => mock.TimeName).Returns("current_time");
            tools.SetupGet(mock => mock.ToolSearchName).Returns("tool_search");
            tools.SetupGet(mock => mock.DelegateName).Returns("delegate");
            tools.Setup(mock => mock.CreateSearch()).Returns(() => Named("search_web"));
            tools.Setup(mock => mock.CreateTime()).Returns(() => Named("current_time"));
            tools.Setup(mock => mock.CreateToolSearch(It.IsAny<Func<IReadOnlyList<PromptTool>>>())).Returns(() => Named("tool_search"));
            tools.Setup(mock => mock.CreateDelegate(It.IsAny<Func<string, bool, CancellationToken, Task<string>>>())).Returns(() => Named("delegate"));
        }
    }

    private static PromptTool Named(string name)
        => new() { Name = name, Description = "d", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement, Handler = (_, _) => Task.FromResult("ok") };
}
