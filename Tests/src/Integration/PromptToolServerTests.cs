namespace Markwardt.LiveAi.Tests.Integration;

public sealed class PromptToolServerTests : IAsyncLifetime
{
    private readonly HttpClient client = new();

    private readonly List<string> calls = [];

    private IPromptToolServer server = null!;

    public Task InitializeAsync()
    {
        PromptTool echo = PromptTool.Create<Details, string>("echo", "Echoes the note.", (input, _) =>
        {
            lock (calls)
            {
                calls.Add(input.Note);
            }

            return Task.FromResult($"echo: {input.Note}");
        });
        PromptTool fail = PromptTool.Create<Details, string>("fail", "Always fails.", (input, _) => throw new InvalidOperationException("it broke"));
        PromptTool slow = new()
        {
            Name = "slow",
            Description = "Waits until the server stops.",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            Handler = async (_, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return "never";
        }
        };
        server = new PromptToolHost().Start([echo, fail, slow], null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        client.Dispose();
    }

    private async Task<HttpResponseMessage> Post(string body, string? token = "")
        => await client.SendAsync(Request(HttpMethod.Post, body, token));

    private HttpRequestMessage Request(HttpMethod method, string? body, string? token)
    {
        HttpRequestMessage request = new(method, server.Endpoint.Url);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {(token.Length == 0 ? server.Endpoint.Token : token)}");
        }

        return request;
    }

    private async Task<JsonElement> Rpc(string method, string parameters = "{}", int id = 1)
    {
        HttpResponseMessage response = await Post($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public void Endpoint_IsALoopbackAddressWithASecretAndTheToolNames()
    {
        Assert.StartsWith("http://127.0.0.1:", server.Endpoint.Url);
        Assert.EndsWith("/mcp", server.Endpoint.Url);
        Assert.Equal(64, server.Endpoint.Token.Length);
        Assert.Equal(["echo", "fail", "slow"], server.Endpoint.Names);
    }

    [Fact]
    public async Task Initialize_AnswersWithTheRequestedProtocolVersionAndTheToolsCapability()
    {
        JsonElement response = await Rpc("initialize", """{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""");

        Assert.Equal("2025-03-26", response.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.True(response.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out JsonElement _));
        Assert.Equal(1, response.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ToolsList_DescribesEachToolWithItsSchema()
    {
        JsonElement tools = (await Rpc("tools/list")).GetProperty("result").GetProperty("tools");

        Assert.Equal(["echo", "fail", "slow"], tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        JsonElement echo = tools[0];
        Assert.Equal("Echoes the note.", echo.GetProperty("description").GetString());
        Assert.Equal("object", echo.GetProperty("inputSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ToolsCall_RunsTheHandlerAndReturnsItsText()
    {
        JsonElement result = (await Rpc("tools/call", """{"name":"echo","arguments":{"note":"hello"}}""")).GetProperty("result");

        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("echo: hello", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(["hello"], calls);
    }

    [Fact]
    public async Task ToolsCall_WhenTheHandlerThrows_ReturnsAnErrorResultTheAiCanSee()
    {
        JsonElement result = (await Rpc("tools/call", """{"name":"fail","arguments":{"note":"x"}}""")).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("it broke", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_WhenTheArgumentsAreWrong_ReturnsAnErrorResult()
    {
        JsonElement result = (await Rpc("tools/call", """{"name":"echo","arguments":{"note":5}}""")).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ToolsCall_WithoutArguments_PassesAnEmptyObject()
    {
        JsonElement result = (await Rpc("tools/call", """{"name":"echo"}""")).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ToolsCall_ForAnUnknownTool_ReturnsAProtocolError()
    {
        JsonElement response = await Rpc("tools/call", """{"name":"nope","arguments":{}}""");

        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_RunsCallsAtTheSameTime()
    {
        string[] notes = Enumerable.Range(0, 20).Select(index => $"note-{index}").ToArray();

        JsonElement[] results = await Task.WhenAll(notes.Select((note, index) => Rpc("tools/call", $$$"""{"name":"echo","arguments":{"note":"{{{note}}}"}}""", index)));

        Assert.Equal(notes.Select(note => $"echo: {note}"), results.Select(result => result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()));
        Assert.Equal(notes.Order(), calls.Order());
    }

    [Fact]
    public async Task Ping_Answers()
        => Assert.Equal(JsonValueKind.Object, (await Rpc("ping")).GetProperty("result").ValueKind);

    [Fact]
    public async Task UnknownMethod_ReturnsAProtocolError()
        => Assert.Equal(-32601, (await Rpc("resources/list")).GetProperty("error").GetProperty("code").GetInt32());

    [Fact]
    public async Task Notification_IsAcceptedWithoutAnswer()
    {
        HttpResponseMessage response = await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not json", -32700)]
    [InlineData("[1]", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":null}""", -32600)]
    public async Task BadRequests_ReturnProtocolErrors(string body, int code)
    {
        HttpResponseMessage response = await Post(body);

        Assert.Equal(code, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret")]
    public async Task Requests_WithoutTheSecret_AreRefused(string? token)
    {
        HttpResponseMessage response = await Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task OtherMethods_AreRefused()
    {
        HttpResponseMessage response = await client.SendAsync(Request(HttpMethod.Get, null, ""));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task DisposeAsync_StopsListeningAndCancelsRunningTools()
    {
        Task<JsonElement> running = Rpc("tools/call", """{"name":"slow","arguments":{}}""");
        await Task.Delay(200);

        await server.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => running);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
    }

    [Fact]
    public async Task Servers_ListenOnDifferentPortsWithDifferentSecrets()
    {
        await using IPromptToolServer other = new PromptToolHost().Start([], null);

        Assert.NotEqual(server.Endpoint.Url, other.Endpoint.Url);
        Assert.NotEqual(server.Endpoint.Token, other.Endpoint.Token);
    }
}
