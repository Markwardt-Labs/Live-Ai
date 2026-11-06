namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptGenerateImageToolTests
{
    private readonly IStore store = IStore.FromMemory();

    private readonly FakeImageGenerator generator = new();

    private PromptTool Tool()
        => new PromptLibraryTools().CreateGenerateImage(store, generator);

    private async Task<string> Run(string json, PromptTool? tool = null)
        => await (tool ?? Tool()).Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);

    private async Task<bool> Has(string path)
    {
        try
        {
            _ = await store.GetSize(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    [Fact]
    public void Create_IsNamedAfterTheReservedNameAndTakesAPromptAndAPath()
    {
        PromptTool tool = Tool();

        Assert.Equal("generate_image", tool.Name);
        Assert.Equal("generate_image", new PromptLibraryTools().GenerateImageName);
        Assert.Equal(["prompt", "path"], tool.InputSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    [Theory]
    [InlineData(PromptImageFeatures.None, new[] { "prompt", "path", "width", "height", "quality", "count" })]
    [InlineData(PromptImageFeatures.Transparency, new[] { "prompt", "path", "width", "height", "quality", "count", "transparent" })]
    [InlineData(PromptImageFeatures.NegativePrompt | PromptImageFeatures.Seed, new[] { "prompt", "path", "width", "height", "quality", "count", "negative_prompt", "seed" })]
    public void Create_OffersOnlyWhatTheGeneratorCanDo(PromptImageFeatures features, string[] expected)
    {
        generator.Features = features;

        Assert.Equal(expected, Tool().InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Create_AddsTheAdviceOfTheGeneratorToTheDescription()
    {
        generator.PromptGuide = "Use short phrases.";

        Assert.EndsWith("Use short phrases.", Tool().Description, StringComparison.Ordinal);
        Assert.Contains("view_image", Tool().Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_SavesTheImageWhereTheAiSaysWithTheExtensionOfWhatWasMade()
    {
        generator.Make = _ => [new PromptImageResult { Content = FakeImageGenerator.Jpeg, Seed = 7 }];

        string text = await Run("""{"prompt":"a fox","path":"art/fox.png"}""");

        Assert.Equal("Saved art/fox.jpg (7 bytes, seed 7).", text);
        Assert.Equal(FakeImageGenerator.Jpeg, await store.ReadBytes("art/fox.jpg"));
        Assert.False(await Has("art/fox.png"));
        Assert.Equal("a fox", generator.Requests.Single().Prompt);
    }

    [Theory]
    [InlineData("fox", "fox.png")]
    [InlineData("fox.PNG", "fox.png")]
    [InlineData("./a//fox", "a/fox.png")]
    public async Task Run_AddsOrCorrectsTheExtensionAndCleansThePath(string asked, string saved)
    {
        await Run($$"""{"prompt":"x","path":"{{asked}}"}""");

        Assert.True(await Has(saved));
    }

    [Fact]
    public async Task Run_NumbersSeveralImagesAndReportsTheirSeeds()
    {
        string text = await Run("""{"prompt":"x","path":"fox.png","count":3}""");

        Assert.Equal("Saved fox-1.png (11 bytes, seed 100).\nSaved fox-2.png (11 bytes, seed 101).\nSaved fox-3.png (11 bytes, seed 102).", text);
        Assert.Equal(3, generator.Requests.Single().Count);
    }

    [Fact]
    public async Task Run_PassesWhatTheGeneratorSupportsAndLeavesOutWhatItDoesNot()
    {
        generator.Features = PromptImageFeatures.Transparency | PromptImageFeatures.Seed;

        await Run("""{"prompt":"x","path":"a.png","width":640,"height":480,"quality":"high","transparent":true,"negative_prompt":"blur","seed":9}""");

        PromptImageRequest request = generator.Requests.Single();
        Assert.Equal((640, 480, PromptImageQuality.High, true, null, 9L), (request.Width, request.Height, request.Quality, request.Transparent, request.NegativePrompt, request.Seed));
    }

    [Fact]
    public async Task Run_ForAGeneratorWithoutFeaturesIgnoresTheArgumentsOfThem()
    {
        await Run("""{"prompt":"x","path":"a.png","transparent":true,"negative_prompt":"blur","seed":9}""");

        PromptImageRequest request = generator.Requests.Single();
        Assert.Equal((false, null, null, PromptImageQuality.Standard), (request.Transparent, request.NegativePrompt, request.Seed, request.Quality));
    }

    [Theory]
    [InlineData("""{"prompt":"x","path":"a.png","count":0}""", 1)]
    [InlineData("""{"prompt":"x","path":"a.png","count":99}""", 4)]
    [InlineData("""{"prompt":"x","path":"a.png"}""", 1)]
    public async Task Run_KeepsTheCountFromOneToFour(string json, int expected)
    {
        await Run(json);

        Assert.Equal(expected, generator.Requests.Single().Count);
    }

    [Fact]
    public async Task Run_KeepsTheSizeWithinWhatIsSensible()
    {
        await Run("""{"prompt":"x","path":"a.png","width":5,"height":99999}""");

        PromptImageRequest request = generator.Requests.Single();
        Assert.Equal((64, 4096), (request.Width, request.Height));
    }

    [Theory]
    [InlineData(500, "500 bytes")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(3 * 1024 * 1024, "3 MB")]
    public async Task Run_SaysTheSizeInUnitsThatDoNotMakeASmallImageLookEmpty(int length, string expected)
    {
        generator.Make = _ => [new PromptImageResult { Content = new byte[length].Select((_, index) => index < FakeImageGenerator.Png.Length ? FakeImageGenerator.Png[index] : (byte)0).ToArray() }];

        Assert.Equal($"Saved a.png ({expected}).", await Run("""{"prompt":"x","path":"a.png"}"""));
    }

    [Fact]
    public async Task Run_NeverReplacesAnImageAndAsksNothingOfTheGeneratorWhenOneIsThere()
    {
        await store.WriteBytes("fox.png", [1]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"x","path":"fox.png"}"""));

        Assert.Contains("already there", exception.Message, StringComparison.Ordinal);
        Assert.Empty(generator.Requests);
        Assert.Equal(new byte[] { 1 }, await store.ReadBytes("fox.png"));
    }

    [Fact]
    public async Task Run_SavesIntoAMountedFolderThroughTheStoreOfTheSession()
    {
        IStore art = IStore.FromMemory();
        MountedStore mounted = new(null, new Dictionary<string, IStore> { ["art"] = art });
        PromptTool tool = new PromptLibraryTools().CreateGenerateImage(mounted, generator);

        string text = await Run("""{"prompt":"x","path":"art/icons/fox.png"}""", tool);

        Assert.Equal("Saved art/icons/fox.png (11 bytes, seed 100).", text);
        Assert.Equal(FakeImageGenerator.Png, await art.ReadBytes("icons/fox.png"));
    }

    [Theory]
    [InlineData("fox.gif", 1)]
    [InlineData("fox.webp", 1)]
    [InlineData("fox-2.png", 3)]
    [InlineData("fox-3.jpg", 3)]
    public async Task Run_RefusesWhenAnyFileItMightWriteIsThereBeforeSpendingAnything(string taken, int count)
    {
        await store.WriteBytes(taken, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run($$"""{"prompt":"x","path":"fox.png","count":{{count}}}"""));

        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task Run_RefusesAPathThatIsEmptyAndAPromptThatIsEmpty()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"x","path":" "}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"  ","path":"a.png"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"path":"a.png"}"""));
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task Run_RefusesSomethingThatIsNotAnImageAndAGeneratorThatMadeNone()
    {
        generator.Make = _ => [new PromptImageResult { Content = new byte[] { 1, 2, 3 } }];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"x","path":"a.png"}"""));
        generator.Make = _ => [];

        InvalidOperationException none = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"x","path":"b.png"}"""));

        Assert.Contains("no image", none.Message, StringComparison.Ordinal);
        Assert.False(await Has("a.png"));
    }

    [Fact]
    public async Task Run_SavesNothingWhenAnyOfSeveralImagesIsNotAnImage()
    {
        generator.Make = _ => [new PromptImageResult { Content = FakeImageGenerator.Png }, new PromptImageResult { Content = new byte[] { 1, 2, 3 } }];

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"x","path":"fox","count":2}"""));

        Assert.False(await Has("fox-1.png"));
    }

    [Fact]
    public async Task Run_NamesTheImagesAlreadySavedWhenALaterOneCannotBe()
    {
        await store.WriteBytes("fox-2.png", [1]);
        generator.Make = _ => [new PromptImageResult { Content = FakeImageGenerator.Png }, new PromptImageResult { Content = FakeImageGenerator.Png }];

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"x","path":"fox"}"""));

        Assert.Contains("fox-2.png", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Saved fox-1.png", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_PassesOnTheReasonAGeneratorFailsWith()
    {
        generator.Make = _ => throw new InvalidOperationException("The service refused the prompt.");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("""{"prompt":"x","path":"a.png"}"""));

        Assert.Equal("The service refused the prompt.", exception.Message);
    }

    [Fact]
    public async Task Run_RefusesToSaveWhereTheStoreDoesNotAllowWriting()
    {
        PromptTool tool = new PromptLibraryTools().CreateGenerateImage(store.AsReadOnly(), generator);

        await Assert.ThrowsAnyAsync<Exception>(() => Run("""{"prompt":"x","path":"a.png"}""", tool));
    }
}
