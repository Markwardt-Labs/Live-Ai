namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptProviderModelsTests
{
    private readonly Mock<IPromptSessionStore> store = new();

    private readonly Mock<IPromptEngine> engine = new();

    private readonly Mock<ITurnRunner> runner = new();

    private readonly Mock<IPromptSessionOpener> opener = new();

    private readonly Mock<IPromptSession> session = new();

    private readonly ManualTime time = new();

    private readonly List<PromptSessionLink> opened = [];

    private readonly List<PromptModel?> openedModels = [];

    private readonly ListLogger logger = new();

    private IReadOnlyList<PromptModel> listing = [];

    public PromptProviderModelsTests()
    {
        engine.SetupGet(mock => mock.Identifier).Returns("Fake");
        engine.Setup(mock => mock.HasSession(It.IsAny<string>())).Returns(true);
        engine.Setup(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(listing));
        store.Setup(mock => mock.Save(It.IsAny<PromptSessionLink>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        opener.Setup(mock => mock.Open(It.IsAny<PromptSessionLink>(), It.IsAny<IPromptProvider>(), It.IsAny<IPromptEngine>(), It.IsAny<string?>(), It.IsAny<PromptModel?>()))
            .Callback<PromptSessionLink, IPromptProvider, IPromptEngine, string?, PromptModel?>((link, _, _, _, model) =>
            {
                opened.Add(link);
                openedModels.Add(model);
            })
            .Returns(session.Object);
    }

    private PromptModel Model(string id, params string[] efforts)
        => new() { Id = id, Settings = efforts.Length == 0 ? [] : [PromptModelSetting.Choose("effort", "Effort", efforts)] };

    private PromptProvider Subject(TimeSpan? lifetime = null)
        => new(engine.Object, store.Object, opener.Object, runner.Object, time, logger) { ModelsLifetime = lifetime ?? TimeSpan.FromMinutes(1) };

    private PromptSessionLink Stored(string? model = "m1", Dictionary<string, string>? settings = null)
    {
        PromptSessionLink link = new() { Id = Guid.NewGuid(), Provider = "Fake", WorkingFolder = AppContext.BaseDirectory, AiSessionId = "abc", Model = model, ModelSettings = settings };
        store.Setup(mock => mock.Find(link.Id, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        return link;
    }

    [Fact]
    public async Task GetModels_AsksTheEngineOnceAndKeepsTheAnswerForItsLifetime()
    {
        listing = [Model("a")];
        PromptProvider subject = Subject();

        IReadOnlyList<PromptModel> first = await subject.GetModels();
        IReadOnlyList<PromptModel> second = await subject.GetModels();
        time.Advance(TimeSpan.FromSeconds(61));
        await subject.GetModels();

        Assert.Same(first, second);
        engine.Verify(mock => mock.GetModels(null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetModels_KeepsTheListOfEachApiKeyApart()
    {
        listing = [Model("a")];
        PromptProvider subject = Subject();

        await subject.GetModels("one");
        await subject.GetModels("two");
        await subject.GetModels("one");

        engine.Verify(mock => mock.GetModels("one", It.IsAny<CancellationToken>()), Times.Once);
        engine.Verify(mock => mock.GetModels("two", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetModels_DoesNotKeepAnEmptyAnswer()
    {
        PromptProvider subject = Subject();

        Assert.Empty(await subject.GetModels());
        listing = [Model("a")];

        Assert.Single(await subject.GetModels());
        engine.Verify(mock => mock.GetModels(null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetModel_FindsAListedModelAndAsksAgainOnceForOneItDoesNotKnow()
    {
        listing = [Model("a")];
        PromptProvider subject = Subject();
        await subject.GetModels();
        listing = [Model("a"), Model("b")];

        Assert.Equal("a", (await subject.GetModel("a")).Id);
        Assert.Equal("b", (await subject.GetModel("b")).Id);
        PromptSessionModelException missing = await Assert.ThrowsAsync<PromptSessionModelException>(() => subject.GetModel("c"));

        Assert.Contains("'c'", missing.Message);
        engine.Verify(mock => mock.GetModels(null, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task GetModel_RefusesAnEmptyIdentifier()
        => await Assert.ThrowsAsync<ArgumentException>(() => Subject().GetModel(" "));

    [Fact]
    public async Task Create_WithAModel_StoresItWithTheDefaultsOfItsSettingsAndOpensTheSessionWithIt()
    {
        PromptModel model = Model("m1", "low", "medium", "high");

        await Subject().Create(new PromptSessionOptions { Model = model });

        PromptSessionLink link = Assert.Single(opened);
        Assert.Equal(("m1", "medium"), (link.Model, link.ModelSettings!["effort"]));
        Assert.Same(model, Assert.Single(openedModels));
        store.Verify(mock => mock.Save(It.Is<PromptSessionLink>(saved => saved.Model == "m1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WithoutAModel_StoresNone()
    {
        await Subject().Create();

        PromptSessionLink link = Assert.Single(opened);
        Assert.Equal((null, null), (link.Model, link.ModelSettings));
        Assert.Null(Assert.Single(openedModels));
    }

    [Fact]
    public async Task Load_OfAStoredModelThatIsStillOffered_OpensItWithItsSettingsBroughtUpToDate()
    {
        listing = [new PromptModel { Id = "m1", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "high"], "low"), PromptModelSetting.Switch("fast", "Fast")] }];
        PromptSessionLink stored = Stored(settings: new Dictionary<string, string> { ["effort"] = "high", ["gone"] = "x" });

        await Subject().Load(stored.Id.ToString("N"));

        PromptSessionLink link = Assert.Single(opened);
        Assert.Equal(("m1", "high", "false"), (link.Model, link.ModelSettings!["effort"], link.ModelSettings["fast"]));
        Assert.DoesNotContain("gone", link.ModelSettings.Keys);
        Assert.Equal("m1", Assert.Single(openedModels)!.Id);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("'gone'") && entry.Message.Contains("dropped"));
    }

    [Fact]
    public async Task Load_ResetsAValueTheModelNoLongerAcceptsAndSaysSo()
    {
        listing = [Model("m1", "low", "high")];
        PromptSessionLink stored = Stored(settings: new Dictionary<string, string> { ["effort"] = "max" });

        await Subject().Load(stored.Id.ToString("N"));

        Assert.Equal("low", Assert.Single(opened).ModelSettings!["effort"]);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("'max'") && entry.Message.Contains("back to"));
    }

    [Fact]
    public async Task Load_OfAStoredModelThatIsGone_OpensTheSessionWithNone()
    {
        listing = [Model("other")];
        PromptSessionLink stored = Stored(settings: new Dictionary<string, string> { ["effort"] = "high" });

        await Subject().Load(stored.Id.ToString("N"));

        PromptSessionLink link = Assert.Single(opened);
        Assert.Equal((null, null), (link.Model, link.ModelSettings));
        Assert.Null(Assert.Single(openedModels));
        engine.Verify(mock => mock.GetModels(null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("m1") && entry.Message.Contains("no longer offered"));
    }

    [Fact]
    public async Task Load_OfAStoredModelThatTheKeptListLacks_AsksTheToolAgainBeforeGivingItUp()
    {
        PromptProvider subject = Subject();
        listing = [Model("other")];
        await subject.GetModels();
        listing = [Model("other"), Model("m1")];
        PromptSessionLink stored = Stored();

        await subject.Load(stored.Id.ToString("N"));

        Assert.Equal("m1", Assert.Single(openedModels)!.Id);
        engine.Verify(mock => mock.GetModels(null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Load_WhenTheToolCannotBeAsked_KeepsTheStoredModelAndItsSettingsAsTheyAre()
    {
        PromptSessionLink stored = Stored(settings: new Dictionary<string, string> { ["effort"] = "high", ["extra"] = "x" });

        await Subject().Load(stored.Id.ToString("N"));

        PromptSessionLink link = Assert.Single(opened);
        Assert.Equal(("m1", "high", "x"), (link.Model, link.ModelSettings!["effort"], link.ModelSettings["extra"]));
        PromptModel model = Assert.Single(openedModels)!;
        Assert.Equal("m1", model.Id);
        Assert.Null(model.Settings);
    }

    [Fact]
    public async Task Load_OfASessionThatHadNoModel_StartsWithNone()
    {
        PromptSessionLink stored = Stored(model: null);

        await Subject().Load(stored.Id.ToString("N"));

        Assert.Equal((null, null), (Assert.Single(opened).Model, Assert.Single(opened).ModelSettings));
        engine.Verify(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Load_WithAModel_RunsThatOneAndKeepsTheValuesBothModelsHave()
    {
        PromptSessionLink stored = Stored(settings: new Dictionary<string, string> { ["effort"] = "high", ["fast"] = "true" });
        PromptModel chosen = new() { Id = "m2", Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "high"], "low"), PromptModelSetting.Count("context", "Context", 1, 10, 5)] };

        await Subject().Load(stored.Id.ToString("N"), new PromptSessionOptions { Model = chosen });

        PromptSessionLink link = Assert.Single(opened);
        Assert.Equal(("m2", "high", "5"), (link.Model, link.ModelSettings!["effort"], link.ModelSettings["context"]));
        Assert.DoesNotContain("fast", link.ModelSettings.Keys);
        engine.Verify(mock => mock.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Load_AsksForTheModelsOfTheApiKeyTheSessionIsLoadedWith()
    {
        listing = [Model("m1")];
        PromptSessionLink stored = Stored();

        await Subject().Load(stored.Id.ToString("N"), new PromptSessionOptions { ApiKey = "key" });

        engine.Verify(mock => mock.GetModels("key", It.IsAny<CancellationToken>()), Times.Once);
    }
}
