namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptSessionModelTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<IPromptProvider> provider = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptToolHost> toolHost = new();

    private readonly Mock<IPromptLibraryTools> libraryTools = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    public PromptSessionModelTests()
    {
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.Plain(It.IsAny<PromptInput>())).Returns((PromptInput turn) => turn.Text);
        engine.Setup(mock => mock.CreateStart(It.IsAny<PromptEngineStart>())).Returns(new ProcessStartInfo("tool"));
        libraryTools.SetupGet(mock => mock.Names).Returns(["read_file"]);
        libraryTools.SetupGet(mock => mock.ShellName).Returns("shell");
        libraryTools.OfferTheEverydayTools();
        libraryTools.Setup(mock => mock.Create(It.IsAny<IStore>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>())).Returns([]);
        toolHost.Setup(mock => mock.Start(It.IsAny<IReadOnlyList<PromptTool>>(), It.IsAny<IPromptToolHooks?>())).Returns(() => Server().Object);
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        runner.Setup(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Done());
    }

    private Mock<IPromptToolServer> Server()
    {
        Mock<IPromptToolServer> server = new();
        server.SetupGet(mock => mock.Endpoint).Returns(new ToolEndpoint { Url = "http://127.0.0.1:1/mcp", Token = "t", Names = [] });
        return server;
    }

    private async IAsyncEnumerable<PromptEngineEvent> Done()
    {
        await Task.Yield();
        yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Finished };
    }

    private PromptSession Session(PromptModel? model, Dictionary<string, string>? settings = null)
        => new(new PromptSessionRecordBuilder { Model = model, ModelSettings = settings ?? model?.Reconcile(new Dictionary<string, string>()).Settings ?? [] }.Build(engine.Object), null, store.Object, provider.Object, engine.Object, runner.Object, toolHost.Object, libraryTools.Object, opener.Object);

    [Fact]
    public async Task EveryCallThatNeedsTheToolRefusesWhileNoModelIsSelected()
    {
        engine.SetupGet(mock => mock.CanPersist).Returns(true);
        PromptSession session = Session(null);

        await Assert.ThrowsAsync<PromptSessionModelException>(() => session.Request("go"));
        await Assert.ThrowsAsync<PromptSessionModelException>(() => session.Send("go"));
        await Assert.ThrowsAsync<PromptSessionModelException>(() => session.Compact());
        await Assert.ThrowsAsync<PromptSessionModelException>(() => session.GetContext());
        await Assert.ThrowsAsync<PromptSessionModelException>(() => session.Request<string>("go"));
        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhatNeedsNoModelStillWorksWhileNoneIsSelected()
    {
        PromptSession session = Session(null);

        Assert.NotEmpty(session.Id);
        Assert.Equal(PromptSessionState.Waiting, session.State);
        Assert.Null(session.Configuration.Model);
        Assert.False(await session.Interrupt());
        session.Configuration.Instructions = "Be brief.";
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ClearingTheModelMakesTheSessionRefuseAgain()
    {
        PromptSession session = Session(TestModels.Model);
        await session.Messages("one");

        session.Configuration.Model = null;

        await Assert.ThrowsAsync<PromptSessionModelException>(() => session.Request("two"));
    }

    [Fact]
    public async Task AModelWhoseSettingsAreUnknownIsReplacedByTheListedOneAtTheFirstPrompt()
    {
        PromptModel listed = new() { Id = "m1", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "high"], "low")] };
        provider.Setup(mock => mock.GetModels(null, It.IsAny<CancellationToken>())).ReturnsAsync([listed]);
        provider.Setup(mock => mock.GetModel("m1", null, It.IsAny<CancellationToken>())).ReturnsAsync(listed);
        PromptSession session = Session(new PromptModel { Id = "m1" }, new Dictionary<string, string> { ["effort"] = "high", ["old"] = "x" });

        await session.Messages("one");

        Assert.Same(listed, session.Configuration.Model);
        Assert.Equal(["effort"], session.Configuration.ModelSettings.Keys);
        Assert.Equal("high", session.Configuration.ModelSettings["effort"]);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Model == "m1" && start.Settings["effort"] == "high" && !start.Settings.ContainsKey("old"))), Times.Once);
    }

    [Fact]
    public async Task AModelTheToolNoLongerOffersFailsThePromptAndLeavesNoneSelected()
    {
        provider.Setup(mock => mock.GetModels(null, It.IsAny<CancellationToken>())).ReturnsAsync([new PromptModel { Id = "other" }]);
        provider.Setup(mock => mock.GetModel("m1", null, It.IsAny<CancellationToken>())).ThrowsAsync(new PromptSessionModelException("not offered"));
        PromptSession session = Session(new PromptModel { Id = "m1" });

        PromptSessionModelException exception = await Assert.ThrowsAsync<PromptSessionModelException>(() => session.Request("go"));

        Assert.Contains("'m1'", exception.Message);
        Assert.Null(session.Configuration.Model);
        runner.Verify(mock => mock.Run(It.IsAny<IPromptEngine>(), It.IsAny<ProcessStartInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AModelTheToolCannotBeAskedAboutIsKeptAndAskedAboutOnlyOnce()
    {
        provider.Setup(mock => mock.GetModels(null, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        PromptSession session = Session(new PromptModel { Id = "m1" }, new Dictionary<string, string> { ["effort"] = "high" });

        await session.Messages("one");
        await session.Messages("two");

        provider.Verify(mock => mock.GetModels(null, It.IsAny<CancellationToken>()), Times.Once);
        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Model == "m1" && start.Settings["effort"] == "high")), Times.Exactly(2));
    }

    [Fact]
    public async Task TheModelIsStoredWhenAPromptStartsWithAnotherOneAndNotAgainWhileItStaysTheSame()
    {
        PromptSession session = Session(TestModels.Model);

        await session.Messages("one");
        store.Verify(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>()), Times.Never);
        session.Configuration.ModelSettings["effort"] = "high";
        await session.Messages("two");
        await session.Messages("three");

        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(link => link.Model == "test-model" && link.ModelSettings!["effort"] == "high"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task APromptIsGivenTheModelAndItsSettingsAsTheyAreWhenItStarts()
    {
        PromptSession session = Session(TestModels.Model);
        session.Configuration.ModelSettings["fast"] = "true";

        await session.Messages("one");

        engine.Verify(mock => mock.CreateStart(It.Is<PromptEngineStart>(start => start.Model == "test-model" && start.Settings["effort"] == "medium" && start.Settings["fast"] == "true")), Times.Once);
    }
}
