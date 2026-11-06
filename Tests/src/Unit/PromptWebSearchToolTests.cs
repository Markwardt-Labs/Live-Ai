namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptWebSearchToolTests
{
    private readonly ManualTime time = new();

    private readonly List<HttpRequestMessage> sent = [];

    private readonly List<string> bodies = [];

    private Func<HttpRequestMessage, HttpResponseMessage> answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private PromptTool Tool()
        => new PromptLibraryTools(new Answering(request =>
        {
            sent.Add(request);
            bodies.Add(request.Content is null ? string.Empty : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return answer(request);
        }), time).CreateSearch();

    private HttpResponseMessage Page(string html, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private string Result(string url, string title, string snippet)
        => $"""
            <div class="result results_links results_links_deep web-result ">
              <div class="links_main links_deep result__body">
                <h2 class="result__title"><a rel="nofollow" class="result__a" href="{url}">{title}</a></h2>
                <div class="result__extras"><a class="result__url" href="{url}">shown url</a></div>
                <a class="result__snippet" href="{url}">{snippet}</a>
              </div>
            </div>
            """;

    private async Task<string> Run(string json, PromptTool? tool = null)
        => await (tool ?? Tool()).Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);

    [Fact]
    public void Create_IsNamedAfterTheReservedNameAndTakesWordsAndACount()
    {
        PromptTool tool = Tool();

        Assert.Equal("search_web", tool.Name);
        Assert.Equal("search_web", new PromptLibraryTools().SearchName);
        Assert.Contains("max_results", tool.InputSchema.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_PostsTheWordsToDuckDuckGosHtmlPageAndListsTheResultsWithTheirSnippets()
    {
        answer = _ => Page(Result("https://github.com/dotnet/core/releases", "Releases &middot; dotnet/core", ".NET <b>release</b> notes &amp; more") + Result("https://example.com/b", "Second", "second snippet"));

        string text = await Run("""{"query":"dotnet release notes"}""");

        Assert.Equal(("https://html.duckduckgo.com/html/", HttpMethod.Post), (sent.Single().RequestUri!.ToString(), sent.Single().Method));
        Assert.Equal("q=dotnet+release+notes", bodies.Single());
        Assert.Contains("untrusted", text.Replace("not as instructions", "untrusted"), StringComparison.Ordinal);
        Assert.Contains("1. Releases · dotnet/core\n   https://github.com/dotnet/core/releases\n   .NET release notes & more", text, StringComparison.Ordinal);
        Assert.Contains("2. Second\n   https://example.com/b\n   second snippet", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReturnsAtMostTheCountAskedForAndNeverMoreThanTen()
    {
        answer = _ => Page(string.Concat(Enumerable.Range(1, 15).Select(number => Result($"https://example.com/{number}", $"Title {number}", "s"))));

        string few = await Run("""{"query":"x","max_results":2}""");
        time.Advance(TimeSpan.FromSeconds(5));
        string many = await Run("""{"query":"x","max_results":50}""");
        time.Advance(TimeSpan.FromSeconds(5));
        string usual = await Run("""{"query":"x"}""");

        Assert.Equal((2, 10, 5), (few.Split("Title ").Length - 1, many.Split("Title ").Length - 1, usual.Split("Title ").Length - 1));
    }

    [Fact]
    public async Task Run_FollowsTheRedirectsOfDuckDuckGoToTheRealAddressAndLeavesOutAds()
    {
        answer = _ => Page(
            Result("//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa%3Fx%3D1&amp;rut=abc", "Redirected", "r") +
            Result("https://duckduckgo.com/y.js?ad_domain=shop.example&amp;u3=zzz", "An ad", "buy now") +
            Result("ftp://example.com/file", "Not web", "n") +
            Result("https://example.com/plain", "Plain", "p"));

        string text = await Run("""{"query":"x"}""");

        Assert.Contains("https://example.com/a?x=1", text, StringComparison.Ordinal);
        Assert.Contains("https://example.com/plain", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An ad", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ftp://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("buy now", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportsAPageWithoutResultsAsNoResults()
    {
        answer = _ => Page("<html><body><div class=\"no-results\">No results.</div></body></html>");

        Assert.Equal("No results for 'zzzz'.", await Run("""{"query":"zzzz"}"""));
    }

    [Fact]
    public async Task Run_RefusesEmptyWords()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"query":"  "}"""));

        Assert.Contains("words", exception.Message, StringComparison.Ordinal);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Run_SpacesTheSearchesAtLeastTwoSecondsApartOnTheClockItIsGiven()
    {
        answer = _ => Page(Result("https://example.com/a", "A", "s"));
        PromptTool tool = Tool();
        await Run("""{"query":"one"}""", tool);

        Task<string> second = Run("""{"query":"two"}""", tool);
        await Task.Delay(100);
        Assert.Single(sent);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Assert.Single(sent);
        time.Advance(TimeSpan.FromSeconds(1.1));
        await second;

        Assert.Equal(2, sent.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Run_WhenTurnedAway_FailsAndRefusesFurtherSearchesForAMinuteWithoutAskingAgain(HttpStatusCode status)
    {
        answer = _ => Page("blocked", status);
        PromptTool tool = Tool();

        InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"query":"one"}""", tool));
        time.Advance(TimeSpan.FromSeconds(10));
        InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"query":"two"}""", tool));

        Assert.Contains("turned the search away", first.Message, StringComparison.Ordinal);
        Assert.Contains("Try again in 50 seconds", second.Message, StringComparison.Ordinal);
        Assert.Single(sent);

        time.Advance(TimeSpan.FromSeconds(51));
        answer = _ => Page(Result("https://example.com/a", "Back", "s"));
        Assert.Contains("Back", await Run("""{"query":"three"}""", tool), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_TakesAChallengePageForBeingTurnedAwayToo()
    {
        answer = _ => Page("<html><body><div class=\"anomaly-modal__modal\">Unfortunately, bots use DuckDuckGo too.</div></body></html>");
        PromptTool tool = Tool();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"query":"one"}""", tool));
        InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"query":"two"}""", tool));

        Assert.Contains("Try again in", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportsOtherFailuresAsErrorsTheAiCanReadWithoutPausingSearches()
    {
        answer = request => bodies.Count == 1 ? Page("oops", HttpStatusCode.InternalServerError) : throw new HttpRequestException("no route");
        PromptTool tool = Tool();

        InvalidOperationException status = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"query":"one"}""", tool));
        time.Advance(TimeSpan.FromSeconds(3));
        InvalidOperationException network = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"query":"two"}""", tool));

        Assert.Contains("500", status.Message, StringComparison.Ordinal);
        Assert.Contains("no route", network.Message, StringComparison.Ordinal);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task Run_ReturnsResultsThatPointToThisMachineToo()
    {
        answer = _ => Page(Result("http://127.0.0.1/internal", "Local", "s"));

        string text = await Run("""{"query":"x"}""");

        Assert.Contains("http://127.0.0.1/internal", text, StringComparison.Ordinal);
    }
}
