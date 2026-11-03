namespace Markwardt.LiveAi.Tests.Integration;

[Collection("Processes")]
public sealed class AttachmentSessionTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private IPromptEngine? claude;

    private IPromptEngine? codex;

    private IPromptEngine Claude => claude ??= new ClaudeEngine(temp.Script("claude", "mkdir -p \"$CLAUDE_CONFIG_DIR/projects/p\"\n: > \"$CLAUDE_CONFIG_DIR/projects/p/claude-session.jsonl\"\nprintf '%s\\n' \"$@\" > \"$PWD/claude-args.txt\"\nIFS= read -r line; printf '%s' \"$line\" > \"$PWD/claude-stdin.txt\"\necho '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"claude saw it\"}]}}'\necho '{\"type\":\"result\",\"is_error\":false,\"session_id\":\"claude-session\"}'"), temp.Combine("claude-state"));

    private IPromptEngine Codex => codex ??= new CodexEngine(new FakeCodex(temp).Script(reply: "codex saw it", before: "for path in $(printf '%s' \"$line\" | grep -o '\"path\":\"[^\"]*\"' | sed 's/\"path\":\"//; s/\"$//'); do cp \"$path\" \"$PWD/seen-$(basename \"$path\")\"; echo \"$path\" >> \"$PWD/codex-image-paths.txt\"; done"), temp.Combine("codex-state"));

    private IPromptProvider Provider(IPromptEngine engine)
        => new PromptProvider(engine, temp.Combine("data"));

    private async Task<IPromptSession> Create(IPromptEngine provider)
        => temp.Track(await Provider(provider).Create(TestModels.Options));

    private PromptAttachment Png(params byte[] content)
        => new() { Name = "shot.png", MediaType = "image/png", Content = content };

    [Fact]
    public async Task Claude_GetsTheImageAsAContentBlockOnStreamInputBeforeTheText()
    {
        await using IPromptSession session = await Create(Claude);

        string reply = await session.Request("what is this?", new PromptRequestOptions { Attachments = [Png(1, 2, 3)] });

        Assert.Equal("claude saw it", reply);
        string[] arguments = await File.ReadAllLinesAsync(temp.Combine("claude-args.txt"));
        Assert.Equal("stream-json", arguments[Array.IndexOf(arguments, "--input-format") + 1]);
        JsonElement message = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("claude-stdin.txt"))).RootElement;
        JsonElement content = message.GetProperty("message").GetProperty("content");
        Assert.Equal(["image", "text"], content.EnumerateArray().Select(block => block.GetProperty("type").GetString()));
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("what is this?", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Claude_GetsAPdfAsADocumentAndATextFileInThePrompt()
    {
        await using IPromptSession session = await Create(Claude);
        PromptAttachment pdf = new() { Name = "a.pdf", MediaType = "application/pdf", Content = new byte[] { 7 } };
        PromptAttachment notes = new() { Name = "notes.txt", MediaType = "text/plain", Content = Encoding.UTF8.GetBytes("remember this") };

        await session.Request("summarize", new PromptRequestOptions { Attachments = [pdf, notes] });

        JsonElement content = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("claude-stdin.txt"))).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(["document", "text"], content.EnumerateArray().Select(block => block.GetProperty("type").GetString()));
        Assert.Equal("summarize\n\nAttached file \"notes.txt\":\n```\nremember this\n```", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Claude_WithOnlyATextFile_KeepsThePlainTextInputOfAnOrdinaryPrompt()
    {
        await using IPromptSession session = await Create(Claude);
        PromptAttachment notes = new() { Name = "notes.txt", MediaType = "text/plain", Content = Encoding.UTF8.GetBytes("remember this") };

        await session.Request("summarize", new PromptRequestOptions { Attachments = [notes] });

        Assert.Equal("summarize\n\nAttached file \"notes.txt\":\n```\nremember this\n```", await temp.ReadPrompt());
    }

    [Fact]
    public async Task Codex_GetsEachImageAsAFileNamedByTheImageOptionThatIsRemovedAfterwards()
    {
        await using IPromptSession session = await Create(Codex);

        string reply = await session.Request("what is this?", new PromptRequestOptions { Attachments = [Png(5, 6, 7), Png(8)] });

        Assert.Equal("codex saw it", reply);
        string[] paths = await File.ReadAllLinesAsync(temp.Combine("codex-image-paths.txt"));
        Assert.Equal(["attachment-0.png", "attachment-1.png"], paths.Select(Path.GetFileName));
        Assert.Equal(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(temp.Combine("seen-attachment-0.png")));
        Assert.Equal(new byte[] { 8 }, await File.ReadAllBytesAsync(temp.Combine("seen-attachment-1.png")));
        JsonElement input = (await new FakeCodex(temp).Parameters("turn/start")).Single().GetProperty("input");
        Assert.Equal(("text", "what is this?"), (input[0].GetProperty("type").GetString(), input[0].GetProperty("text").GetString()));
        Assert.Equal(["localImage", "localImage"], input.EnumerateArray().Skip(1).Select(item => item.GetProperty("type").GetString()));
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.All(paths, path => Assert.False(Directory.Exists(Path.GetDirectoryName(path))));
    }

    [Fact]
    public async Task Codex_ResumesTheSessionWithTheImageOnTheSecondPrompt()
    {
        await using IPromptSession session = await Create(Codex);
        await session.Request("first");

        await session.Request("second", new PromptRequestOptions { Attachments = [Png(1)] });

        FakeCodex fake = new(temp);
        Assert.Equal("codex-thread", Assert.Single(await fake.Parameters("thread/resume")).GetProperty("threadId").GetString());
        Assert.Equal("localImage", (await fake.Parameters("turn/start"))[1].GetProperty("input")[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Codex_RefusesAPdfWithoutStartingAnything()
    {
        await using IPromptSession session = await Create(Codex);
        PromptAttachment pdf = new() { Name = "a.pdf", MediaType = "application/pdf", Content = new byte[] { 7 } };

        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(() => session.Request("summarize", new PromptRequestOptions { Attachments = [pdf] }));

        Assert.Contains("Codex cannot be given the attachment 'a.pdf'", exception.Message);
        Assert.False(File.Exists(temp.Combine("codex-requests.txt")));
    }

    [Fact]
    public async Task AnAttachmentTheLibraryCannotGive_IsRefusedForBothProvidersAndTheSessionStillWorks()
    {
        foreach (IPromptEngine provider in new[] { Claude, Codex })
        {
            await using IPromptSession session = await Create(provider);
            PromptAttachment zip = new() { Name = "a.zip", MediaType = "application/zip", Content = new byte[] { 1 } };

            await Assert.ThrowsAsync<NotSupportedException>(() => session.Request("open it", new PromptRequestOptions { Attachments = [zip] }));

            Assert.Equal(provider == Claude ? "claude saw it" : "codex saw it", await session.Request("hello"));
        }
    }

    [Fact]
    public async Task AFileOfAStoreCanBeAttachedAfterReadingIt()
    {
        IStore store = IStore.FromMemory();
        await store.WriteBytes("pics/shot.png", [4, 4]);
        await using IPromptSession session = await Create(Claude);

        await session.Request("look", new PromptRequestOptions { Attachments = [await store.ReadAttachment("pics/shot.png")] });

        JsonElement content = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("claude-stdin.txt"))).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(Convert.ToBase64String([4, 4]), content[0].GetProperty("source").GetProperty("data").GetString());
    }
}
