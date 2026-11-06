namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptFetchToolTests
{
    private Func<HttpRequestMessage, HttpResponseMessage> answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private async Task<string> Run(string json)
    {
        PromptTool tool = new PromptLibraryTools(new Answering(request => answer(request))).CreateFetch();
        return await tool.Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);
    }

    private HttpResponseMessage Page(string type, string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Fact]
    public void Create_IsNamedAfterTheReservedName()
    {
        PromptTool tool = new PromptLibraryTools().CreateFetch();

        Assert.Equal("fetch_url", tool.Name);
        Assert.Equal("fetch_url", new PromptLibraryTools().FetchName);
        Assert.Contains("max_chars", tool.InputSchema.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReturnsPlainTextAsItIsAndHtmlReducedToItsReadableText()
    {
        answer = request => request.RequestUri!.AbsolutePath == "/page"
            ? Page("text/html", "<html><head><title>T</title><style>p{}</style></head><body><script>var x=1;</script><h1>Hello</h1><p>a &amp; b</p><!-- c --></body></html>")
            : Page("application/json", """{"a":1}""");

        string html = await Run("""{"url":"https://example.com/page"}""");
        string json = await Run("""{"url":"https://example.com/data"}""");

        Assert.Equal("Hello\na & b", html.Replace("\r", string.Empty).Replace("\n\n", "\n"));
        Assert.Equal("""{"a":1}""", json);
    }

    [Fact]
    public async Task Run_ReadsTextInTheCharsetTheServerNamesAndFallsBackToUtf8ForOneItDoesNotKnow()
    {
        answer = request => request.RequestUri!.AbsolutePath == "/latin"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.Latin1.GetBytes("caf\u00e9")) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "iso-8859-1" } } } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("caf\u00e9")) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "no-such-charset" } } } };

        Assert.Equal("caf\u00e9", await Run("""{"url":"https://example.com/latin"}"""));
        Assert.Equal("caf\u00e9", await Run("""{"url":"https://example.com/odd"}"""));
    }

    [Fact]
    public async Task Run_ReadsAtMostTwoMegabytesAndDoesNotEndOnABrokenCharacter()
    {
        answer = _ => Page("text/plain", new string('\u00e9', 1_500_000));

        string text = await Run("""{"url":"https://example.com/huge","max_chars":200000}""");

        Assert.DoesNotContain('\uFFFD', text);
        Assert.StartsWith("\u00e9\u00e9", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReadsAnyAddressWithoutRestrictionAndFollowsARedirectToOneOnThisMachine()
    {
        List<string> asked = [];
        answer = request =>
        {
            asked.Add(request.RequestUri!.ToString());
            return request.RequestUri.AbsolutePath == "/start"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://127.0.0.1:8080/secret") } }
                : Page("text/plain", "reached");
        };

        string local = await Run("""{"url":"http://192.168.1.10/admin"}""");
        string redirected = await Run("""{"url":"https://example.com/start"}""");

        Assert.Equal(("reached", "reached"), (local, redirected));
        Assert.Equal(["http://192.168.1.10/admin", "https://example.com/start", "http://127.0.0.1:8080/secret"], asked);
    }

    [Fact]
    public async Task Run_FollowsRedirectsUpToAFewAndGivesUpOnALoopOrAnAddressThatIsNotHttp()
    {
        answer = request => request.RequestUri!.AbsolutePath switch
        {
            "/final" => Page("text/plain", "arrived"),
            "/one" => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://example.com/final") } },
            "/ftp" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("ftp://example.com/file") } },
            _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/loop") } }
        };

        Assert.Equal("arrived", await Run("""{"url":"https://example.com/one"}"""));
        Assert.Contains("more than", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/loop"}"""))).Message, StringComparison.Ordinal);
        Assert.Contains("not an http", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/ftp"}"""))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_CutsLongTextAtTheLimitAndSaysSo()
    {
        answer = _ => Page("text/plain", new string('x', 500));

        string text = await Run("""{"url":"http://example.com/long","max_chars":100}""");

        Assert.Equal(new string('x', 100) + "\n[cut after 100 characters]", text);
    }

    [Theory]
    [InlineData("""{"url":"file:///etc/passwd"}""")]
    [InlineData("""{"url":"nonsense"}""")]
    public async Task Run_RefusesAnythingButAbsoluteWebAddresses(string json)
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(json));

        Assert.Contains("absolute http or https", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportsAnErrorStatusContentThatIsNotTextAndANetworkFailureAsErrorsTheAiCanRead()
    {
        answer = request => request.RequestUri!.AbsolutePath switch
        {
            "/bin" => Page("image/png", "x"),
            "/down" => throw new HttpRequestException("no route"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        InvalidOperationException missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/none"}"""));
        InvalidOperationException binary = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/bin"}"""));
        InvalidOperationException down = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"url":"https://example.com/down"}"""));

        Assert.Contains("404", missing.Message, StringComparison.Ordinal);
        Assert.Contains("not text", binary.Message, StringComparison.Ordinal);
        Assert.Contains("no route", down.Message, StringComparison.Ordinal);
    }
}
