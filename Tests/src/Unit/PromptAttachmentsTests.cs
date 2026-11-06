namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptAttachmentsTests
{
    private readonly ClaudeEngine claude = new("claude");

    private readonly CodexEngine codex = new("codex");

    private PromptAttachment Attachment(string name, string mediaType, byte[]? content = null)
        => new() { Name = name, MediaType = mediaType, Content = content ?? [1, 2, 3] };

    private PromptAttachment Text(string name, string text, string mediaType = "text/plain")
        => Attachment(name, mediaType, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Prepare_GivesImagesToTheProviderAsTheyAreAndLeavesThePromptAlone()
    {
        PromptAttachment image = Attachment("a.png", "image/png");

        (string prompt, IReadOnlyList<PromptAttachment> binary) = new[] { image }.AsReadOnly().Prepare("look", codex);

        Assert.Equal("look", prompt);
        Assert.Equal([image], binary);
    }

    [Fact]
    public void Prepare_PutsTextFilesIntoThePromptAfterItInOrderWithTheirNames()
    {
        (string prompt, IReadOnlyList<PromptAttachment> binary) = new[] { Text("notes.md", "first\n", "text/markdown"), Text("data.json", "{\"a\":1}", "application/json") }.AsReadOnly().Prepare("read these", claude);

        Assert.Equal("read these\n\nAttached file \"notes.md\":\n```\nfirst\n```\n\nAttached file \"data.json\":\n```\n{\"a\":1}\n```", prompt);
        Assert.Empty(binary);
    }

    [Fact]
    public void Prepare_UsesAFenceThatTheContentCannotEnd()
    {
        (string prompt, _) = new[] { Text("code.md", "before\n```\ninside\n````\nafter") }.AsReadOnly().Prepare("go", claude);

        Assert.Contains("\n`````\nbefore", prompt);
        Assert.EndsWith("after\n`````", prompt);
    }

    [Fact]
    public void Prepare_TakesAPdfForClaudeButNotForCodex()
    {
        PromptAttachment pdf = Attachment("a.pdf", "application/pdf");

        Assert.Equal([pdf], new[] { pdf }.AsReadOnly().Prepare("go", claude).Binary);
        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => new[] { pdf }.AsReadOnly().Prepare("go", codex));
        Assert.Contains("Codex cannot be given the attachment 'a.pdf' of type 'application/pdf'", exception.Message);
        Assert.DoesNotContain("PDF documents", exception.Message);
    }

    [Fact]
    public void Prepare_RefusesATypeThatIsNeitherShownNorText_AndSaysWhatToDoInstead()
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => new[] { Attachment("a.zip", "application/zip") }.AsReadOnly().Prepare("go", claude));

        Assert.Contains("Claude Code cannot be given the attachment 'a.zip'", exception.Message);
        Assert.Contains("and so are PDF documents", exception.Message);
        Assert.Contains("put anything else in the session's cache", exception.Message);
    }

    [Fact]
    public void Prepare_RefusesATextFileThatIsNotUtf8OrIsTooLargeForAPrompt()
    {
        Assert.Throws<ArgumentException>(() => new[] { Attachment("bad.txt", "text/plain", [0xFF, 0xFE, 0xFD]) }.AsReadOnly().Prepare("go", claude));
        ArgumentException large = Assert.Throws<ArgumentException>(() => new[] { Attachment("big.txt", "text/plain", new byte[300 * 1024]) }.AsReadOnly().Prepare("go", claude));

        Assert.Contains("Put it in the session's cache instead", large.Message);
    }

    [Fact]
    public void Prepare_RefusesTooManyAndTooLargeAttachments()
    {
        PromptAttachment image = Attachment("a.png", "image/png");

        Assert.Throws<ArgumentException>(() => Enumerable.Repeat(image, 21).ToArray().AsReadOnly().Prepare("go", claude));
        Assert.Equal(20, Enumerable.Repeat(image, 20).ToArray().AsReadOnly().Prepare("go", claude).Binary.Count);
        Assert.Throws<ArgumentException>(() => new[] { Attachment("huge.png", "image/png", new byte[21 * 1024 * 1024]) }.AsReadOnly().Prepare("go", claude));
        Assert.Throws<ArgumentException>(() => new[] { Attachment("a.png", "image/png", new byte[16 * 1024 * 1024]), Attachment("b.png", "image/png", new byte[16 * 1024 * 1024]) }.AsReadOnly().Prepare("go", claude));
    }

    [Theory]
    [InlineData("photo.PNG", "image/png")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("doc.pdf", "application/pdf")]
    [InlineData("readme.md", "text/markdown")]
    [InlineData("data.json", "application/json")]
    [InlineData("config.yml", "application/yaml")]
    [InlineData("noextension", "application/octet-stream")]
    [InlineData("archive.zip", "application/octet-stream")]
    public void ToMediaType_FollowsTheExtension(string name, string expected)
        => Assert.Equal(expected, name.ToMediaType());

    [Theory]
    [InlineData("text/plain", true)]
    [InlineData("TEXT/CSV", true)]
    [InlineData("application/json", true)]
    [InlineData("application/vnd.api+json", true)]
    [InlineData("application/xml", true)]
    [InlineData("image/png", false)]
    [InlineData("application/pdf", false)]
    [InlineData("application/octet-stream", false)]
    public void IsText_RecognizesTextTypes(string mediaType, bool expected)
        => Assert.Equal(expected, mediaType.IsText());

    [Fact]
    public async Task FromFile_ReadsTheBytesNameAndMediaType()
    {
        using TempFolder temp = new();
        await File.WriteAllBytesAsync(temp.Combine("shot.png"), [9, 8, 7]);

        PromptAttachment attachment = await PromptAttachment.FromFile(temp.Combine("shot.png"));

        Assert.Equal("shot.png", attachment.Name);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal(new byte[] { 9, 8, 7 }, attachment.Content.ToArray());
        await Assert.ThrowsAsync<FileNotFoundException>(() => PromptAttachment.FromFile(temp.Combine("missing.png")));
    }

    [Fact]
    public async Task ReadAttachment_ReadsAFileOfAStoreNamedAfterItsLastPart()
    {
        IStore store = new MemoryStore();
        await store.WriteBytes("images/deep/photo.jpg", [1, 2]);

        PromptAttachment attachment = await store.ReadAttachment("images/deep/photo.jpg");

        Assert.Equal("photo.jpg", attachment.Name);
        Assert.Equal("image/jpeg", attachment.MediaType);
        Assert.Equal(new byte[] { 1, 2 }, attachment.Content.ToArray());
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ReadAttachment("nope.png"));
    }
}
