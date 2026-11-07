namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionAskerTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<IPromptToolService> provider = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptToolHost> toolHost = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private readonly List<IReadOnlyList<PromptTool>> served = [];

    public PromptSessionAskerTests()
    {
        store.Setup(mock => mock.GetWorkspaceFolder(It.IsAny<Guid>())).Returns((Guid id) => Path.Combine(Path.GetTempPath(), $"markwardt.liveai-{id:N}"));
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.Plain(It.IsAny<PromptInput>())).Returns((PromptInput turn) => turn.Text);
        engine.Setup(mock => mock.CreateStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("tool"));
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>())).Callback<IReadOnlyList<PromptTool>>(tools => served.Add(tools)).Returns(() => Server().Object);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Events());
        provider.Setup(mock => mock.Select(It.IsAny<PromptCapability>(), It.IsAny<PromptEffort>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((TestModels.Model, new Dictionary<string, string>()));
    }

    private PromptSession Session()
        => new(new PromptSessionRecordBuilder().Build(engine.Object), null, store.Object, provider.Object, engine.Object, runner.Object, toolHost.Object, new PromptLibraryTools(), opener.Object);

    private Mock<IPromptToolServer> Server()
    {
        Mock<IPromptToolServer> server = new();
        server.SetupGet(mock => mock.Endpoint).Returns(new ToolEndpoint { Url = "http://127.0.0.1:1/mcp", Token = "t", Names = [] });
        return server;
    }

    private async IAsyncEnumerable<PromptEngineEvent> Events()
    {
        await Task.Yield();
        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = "done" };
        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished, Text = string.Empty };
    }

    private Task<string> Call(PromptTool tool, string arguments)
        => tool.Handler(JsonDocument.Parse(arguments).RootElement, CancellationToken.None);

    [Fact]
    public async Task WithoutAnAsker_TheAiIsOfferedNoQuestionTool()
    {
        await Session().Request("go");

        Assert.DoesNotContain(served.Single(), tool => tool.Name == "ask_user");
    }

    [Fact]
    public async Task WithAnAsker_TheAiIsOfferedTheQuestionToolAndItsQuestionsReachTheAsker()
    {
        PromptSession session = Session();
        string? asked = null;
        session.Transient.Asker = (question, _) =>
        {
            asked = question;
            return Task.FromResult("blue");
        };

        await session.Request("go");

        PromptTool ask = Assert.Single(served.Single(), tool => tool.Name == "ask_user");
        Assert.Equal("blue", await Call(ask, """{"question":"Which colour?"}"""));
        Assert.Equal("Which colour?", asked);
        Assert.Contains("question", ask.InputSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public async Task TheAskerIsReadWhenTheQuestionArrives_SoClearingItLeavesAnErrorForTheAi()
    {
        PromptSession session = Session();
        session.Transient.Asker = (_, _) => Task.FromResult("yes");
        await session.Request("go");
        PromptTool ask = served.Single().Single(tool => tool.Name == "ask_user");

        session.Transient.Asker = null;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(ask, """{"question":"Sure?"}"""));
        Assert.Contains("No one is available", error.Message);
    }

    [Fact]
    public async Task AnAskerThatThrows_FailsTheCallAndANullReplyBecomesEmptyText()
    {
        PromptSession session = Session();
        session.Transient.Asker = (question, _) => question == "boom" ? throw new IOException("window closed") : Task.FromResult<string>(null!);
        await session.Request("go");
        PromptTool ask = served.Single().Single(tool => tool.Name == "ask_user");

        await Assert.ThrowsAsync<IOException>(() => Call(ask, """{"question":"boom"}"""));
        Assert.Equal(string.Empty, await Call(ask, """{"question":"quiet"}"""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Call(ask, """{"question":"  "}"""));
    }

    [Fact]
    public async Task SettingOrClearingTheAsker_ChangesWhatIsServedAndSettingItAgainDoesNotRestartTheServer()
    {
        PromptSession session = Session();
        PromptAsker asker = (_, _) => Task.FromResult("a");

        await session.Request("one");
        session.Transient.Asker = asker;
        await session.Request("two");
        session.Transient.Asker = (_, _) => Task.FromResult("b");
        await session.Request("three");
        session.Transient.Asker = null;
        await session.Request("four");

        Assert.Equal([false, true, false], served.Select(tools => tools.Any(tool => tool.Name == "ask_user")));
    }

    [Fact]
    public void TheNamesOfTheLibrarysOwnToolsAreTakenWhateverTheirCase()
    {
        PromptSession session = Session();

        foreach (string name in new[] { "ask_user", "ASK_USER", "respond", "Respond", "shell", "Read_File" })
        {
            Assert.Throws<ArgumentException>(() => session.Transient.Tools.Add(new PromptTool { Name = name, Description = "x", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement, Handler = (_, _) => Task.FromResult("ok") }));
        }
    }
}
