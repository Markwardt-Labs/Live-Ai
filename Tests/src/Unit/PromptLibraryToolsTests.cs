namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptLibraryToolsTests
{
    private readonly PromptLibraryTools library = new();

    private readonly FakeStore files = new(new Dictionary<string, string>
    {
        ["src/Config.cs"] = "class Config\n{\n    const int RetryLimit = 10;\n}\n",
        ["src/Client.cs"] = "class Client { int n = Config.RetryLimit; }\n",
        ["src/deep/Worker.cs"] = "// Uses RetryLimit\r\nclass Worker { }\r\n",
        ["docs/notes.md"] = "Notes mention RetryLimit and retrylimit.\n",
        ["README.md"] = "# Demo\n",
        [".git/config"] = "RetryLimit in git internals\n"
    });

    private async Task<string> Run(string name, string json)
    {
        PromptTool tool = library.Create(files, [], false).Single(candidate => candidate.Name == name);
        return await tool.Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);
    }

    [Fact]
    public void Create_ProvidesTheNamedTools()
    {
        IReadOnlyList<PromptTool> tools = library.Create(files, [], false);

        Assert.Equal(library.Names, tools.Select(tool => tool.Name));
    }

    [Fact]
    public void Create_DescribesTheInputsInSnakeCase()
    {
        PromptTool edit = library.Create(files, [], false).Single(tool => tool.Name == "edit_file");

        JsonElement properties = edit.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("old_string", out JsonElement _));
        Assert.True(properties.TryGetProperty("new_string", out JsonElement _));
        Assert.True(properties.TryGetProperty("replace_all", out JsonElement _));
        Assert.Equal(["path", "old_string", "new_string"], edit.InputSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public async Task ListDirectory_ListsFoldersFirstWithASlash()
    {
        Assert.Equal(".git/\ndocs/\nsrc/\nREADME.md", await Run("list_directory", "{}"));
        Assert.Equal("deep/\nClient.cs\nConfig.cs", await Run("list_directory", """{"path":"src"}"""));
    }

    [Fact]
    public async Task ListDirectory_ForAnEmptyFolder_SaysSo()
        => Assert.Equal("(empty)", await Run("list_directory", """{"path":"nothing"}"""));

    [Theory]
    [InlineData("""{"path":"src/"}""")]
    [InlineData("""{"path":"./src/deep/.."}""")]
    [InlineData("""{"path":"src\\"}""")]
    public async Task Paths_AreNormalized(string json)
        => Assert.Equal("deep/\nClient.cs\nConfig.cs", await Run("list_directory", json));

    [Theory]
    [InlineData("/etc/hostname")]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/work/src/Config.cs")]
    public async Task Paths_AbsoluteOrLeadingOutside_AreRefusedWhenThereIsNoWorkspaceToTakeThem(string path)
    {
        IStore mounted = new MountedStore(null, new Dictionary<string, IStore> { ["work"] = files });
        Task<string> Call(string name, string json)
            => library.Create(mounted, ["work"], false).Single(candidate => candidate.Name == name).Handler(JsonDocument.Parse(json).RootElement, default);

        await Assert.ThrowsAsync<FileNotFoundException>(() => Call("read_file", $$"""{"path":"{{path}}"}"""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call("write_file", $$"""{"path":"{{path}}","content":"x"}"""));
        Assert.DoesNotContain(files.Files.Keys, key => key.Contains("secret") || key.Contains("hostname") || key.Contains("win.ini"));
    }

    [Theory]
    [InlineData("/etc/hostname")]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    public async Task Paths_AbsoluteOrLeadingOutside_AreLeftToTheStoreThatTheyAreGivenTo(string path)
    {
        Mock<IStore> store = new();
        store.Setup(mock => mock.Open(path, StoreOpenMode.Read, It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("the store refuses"));

        UnauthorizedAccessException exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.Create(store.Object, [], true).Single(candidate => candidate.Name == "read_file").Handler(JsonDocument.Parse($$"""{"path":"{{path}}"}""").RootElement, default));

        Assert.Equal("the store refuses", exception.Message);
    }

    [Fact]
    public async Task FindFiles_MatchesNamesAtAnyDepthWhenThePatternHasNoSlash()
        => Assert.Equal("src/Client.cs\nsrc/Config.cs\nsrc/deep/Worker.cs", await Run("find_files", """{"pattern":"*.cs"}"""));

    [Fact]
    public async Task FindFiles_MatchesThePathWhenThePatternHasASlash()
    {
        Assert.Equal("src/Client.cs\nsrc/Config.cs", await Run("find_files", """{"pattern":"src/*.cs"}"""));
        Assert.Equal("src/Client.cs\nsrc/Config.cs\nsrc/deep/Worker.cs", await Run("find_files", """{"pattern":"src/**/*.cs"}"""));
        Assert.Equal("src/deep/Worker.cs", await Run("find_files", """{"pattern":"**/Work?r.cs"}"""));
    }

    [Fact]
    public async Task FindFiles_SkipsGitInternalsAndCanBeScopedToAFolder()
    {
        Assert.DoesNotContain("config", await Run("find_files", """{"pattern":"*"}""").ContinueWith(task => task.Result.Replace("src/Config.cs", string.Empty)));
        Assert.Equal("src/Client.cs\nsrc/Config.cs\nsrc/deep/Worker.cs", await Run("find_files", """{"pattern":"*","path":"src"}"""));
    }

    [Fact]
    public async Task FindFiles_WithoutAMatch_SaysSo()
        => Assert.Equal("No files found.", await Run("find_files", """{"pattern":"*.rs"}"""));

    [Fact]
    public async Task SearchFiles_ReturnsPathLineAndText()
    {
        string result = await Run("search_files", """{"pattern":"RetryLimit"}""");

        Assert.Equal(["docs/notes.md:1:Notes mention RetryLimit and retrylimit.", "src/Client.cs:1:class Client { int n = Config.RetryLimit; }", "src/Config.cs:3:    const int RetryLimit = 10;", "src/deep/Worker.cs:1:// Uses RetryLimit"], result.Split('\n'));
    }

    [Fact]
    public async Task SearchFiles_CanIgnoreCaseAndBeLimitedByFolderAndGlob()
    {
        Assert.Equal("docs/notes.md:1:Notes mention RetryLimit and retrylimit.", await Run("search_files", """{"pattern":"retrylimit","ignore_case":true,"glob":"*.md"}"""));
        Assert.Equal("No matches.", await Run("search_files", """{"pattern":"RETRYLIMIT"}"""));
        Assert.Equal("src/Client.cs:1:class Client { int n = Config.RetryLimit; }\nsrc/Config.cs:3:    const int RetryLimit = 10;", (await Run("search_files", """{"pattern":"RetryLimit","path":"src","glob":"src/*.cs"}""")));
    }

    [Fact]
    public async Task SearchFiles_SkipsBinaryFilesAndFilesItCannotRead()
    {
        files.Files["bin/data.dat"] = "RetryLimit\0binary";

        Assert.DoesNotContain("data.dat", await Run("search_files", """{"pattern":"RetryLimit"}"""));
    }

    [Fact]
    public async Task SearchFiles_StopsAtTheMatchLimit()
    {
        files.Files["big.txt"] = string.Join('\n', Enumerable.Repeat("match", 500));

        string result = await Run("search_files", """{"pattern":"match"}""");

        Assert.Equal(201, result.Split('\n').Length);
        Assert.EndsWith("(stopped at 200 matches)", result);
    }

    [Fact]
    public async Task SearchFiles_ShortensVeryLongLines()
    {
        files.Files["long.txt"] = "match " + new string('x', 1000);

        Assert.Contains("... (", await Run("search_files", """{"pattern":"match","path":"","glob":"long.txt"}"""));
    }

    [Fact]
    public async Task ReadFile_NumbersTheLines()
        => Assert.Equal("1\tclass Config\n2\t{\n3\t    const int RetryLimit = 10;\n4\t}", await Run("read_file", """{"path":"src/Config.cs"}"""));

    [Fact]
    public async Task ReadFile_TrimsWindowsLineEndings()
        => Assert.Equal("1\t// Uses RetryLimit\n2\tclass Worker { }", await Run("read_file", """{"path":"src/deep/Worker.cs"}"""));

    [Fact]
    public async Task ReadFile_ReadsAPartAndSaysWhatIsLeft()
    {
        string result = await Run("read_file", """{"path":"src/Config.cs","offset":2,"limit":2}""");

        Assert.Equal("2\t{\n3\t    const int RetryLimit = 10;\n(showing lines 2 to 3 of 4; use offset to read more)", result);
    }

    [Fact]
    public async Task ReadFile_CapsTheDefaultNumberOfLines()
    {
        files.Files["many.txt"] = string.Join('\n', Enumerable.Range(1, 2500));

        string result = await Run("read_file", """{"path":"many.txt"}""");

        Assert.Contains("2000\t2000", result);
        Assert.DoesNotContain("2001\t", result);
        Assert.EndsWith("(showing lines 1 to 2000 of 2500; use offset to read more)", result);
    }

    [Fact]
    public async Task ReadFile_PastTheEnd_SaysSoAndAnEmptyFileIsEmpty()
    {
        files.Files["empty.txt"] = string.Empty;

        Assert.Equal("(the file has 4 lines; offset 9 is past the end)", await Run("read_file", """{"path":"src/Config.cs","offset":9}"""));
        Assert.Equal("(empty file)", await Run("read_file", """{"path":"empty.txt"}"""));
    }

    [Fact]
    public async Task ReadFile_ShortensVeryLongLines()
    {
        files.Files["long.txt"] = new string('x', 5000);

        Assert.Contains("... (3000 more characters)", await Run("read_file", """{"path":"long.txt"}"""));
    }

    [Fact]
    public async Task ReadFile_RefusesBinaryFilesAndPassesOnTheStoresError()
    {
        files.Files["data.dat"] = "a\0b";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("read_file", """{"path":"data.dat"}"""));
        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() => Run("read_file", """{"path":"missing.txt"}"""));
        Assert.Equal("No such file: missing.txt", exception.Message);
    }

    [Fact]
    public async Task WriteFile_WritesAndReportsTheLines()
    {
        Assert.Equal("Wrote notes/new.txt (3 lines).", await Run("write_file", """{"path":"notes/new.txt","content":"a\nb\nc"}"""));
        Assert.Equal("a\nb\nc", files.Files["notes/new.txt"]);
    }

    [Fact]
    public async Task WriteFile_WithoutAPath_Throws()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Run("write_file", """{"path":"","content":"x"}"""));

    [Fact]
    public async Task EditFile_ReplacesTheOnlyOccurrence()
    {
        Assert.Equal("Edited src/Config.cs (1 replacement).", await Run("edit_file", """{"path":"src/Config.cs","old_string":"RetryLimit = 10","new_string":"RetryLimit = 20"}"""));
        Assert.Contains("RetryLimit = 20", files.Files["src/Config.cs"]);
    }

    [Fact]
    public async Task EditFile_WhenTheTextAppearsMoreThanOnce_NeedsReplaceAll()
    {
        files.Files["twice.txt"] = "x y x";

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("edit_file", """{"path":"twice.txt","old_string":"x","new_string":"z"}"""));

        Assert.Contains("matches 2 times", exception.Message);
        Assert.Contains("replace_all", exception.Message);
        Assert.Equal("x y x", files.Files["twice.txt"]);
        Assert.Equal("Edited twice.txt (2 replacements).", await Run("edit_file", """{"path":"twice.txt","old_string":"x","new_string":"z","replace_all":true}"""));
        Assert.Equal("z y z", files.Files["twice.txt"]);
    }

    [Fact]
    public async Task EditFile_WhenTheTextIsNotThere_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("edit_file", """{"path":"src/Config.cs","old_string":"nope","new_string":"x"}"""));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Run("edit_file", """{"path":"missing.txt","old_string":"a","new_string":"b"}"""));
    }

    [Theory]
    [InlineData("""{"path":"src/Config.cs","old_string":"","new_string":"x"}""")]
    [InlineData("""{"path":"src/Config.cs","old_string":"class","new_string":"class"}""")]
    public async Task EditFile_WithoutARealChange_Throws(string json)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => Run("edit_file", json));

    [Fact]
    public async Task EditFile_KeepsTheRestOfTheFileExactly()
    {
        files.Files["crlf.txt"] = "a\r\nb\r\nc\r\n";

        await Run("edit_file", """{"path":"crlf.txt","old_string":"b","new_string":"B"}""");

        Assert.Equal("a\r\nB\r\nc\r\n", files.Files["crlf.txt"]);
    }

    [Fact]
    public async Task DeleteFile_DeletesAndRefusesTheWorkspace()
    {
        Assert.Equal("Deleted README.md.", await Run("delete_file", """{"path":"README.md"}"""));
        Assert.DoesNotContain("README.md", files.Files.Keys);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("delete_file", """{"path":""}"""));
    }

    [Fact]
    public async Task MoveFile_MovesAndNeedsBothPaths()
    {
        Assert.Equal("Moved README.md to docs/readme.md.", await Run("move_file", """{"from":"README.md","to":"docs/readme.md"}"""));
        Assert.Equal("# Demo\n", files.Files["docs/readme.md"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("move_file", """{"from":"docs/readme.md","to":""}"""));
    }

    [Fact]
    public async Task Walking_VisitsFilesInDeepFolders()
    {
        files.Files["a/b/c/d/e.txt"] = "needle";

        Assert.Equal("a/b/c/d/e.txt:1:needle", await Run("search_files", """{"pattern":"needle"}"""));
    }

    [Fact]
    public void Names_AreValidToolNames()
        => Assert.All(library.Names, name => Assert.Matches("^[A-Za-z0-9_-]{1,64}$", name));

    [Fact]
    public async Task CopyFile_CopiesAFileAndAFolderAndLeavesTheOriginals()
    {
        Assert.Equal("Copied README.md to docs/readme-copy.md.", await Run("copy_file", """{"from":"README.md","to":"docs/readme-copy.md"}"""));
        Assert.Equal("Copied src/deep to backup.", await Run("copy_file", """{"from":"src/deep","to":"backup"}"""));

        Assert.Equal("# Demo\n", files.Files["docs/readme-copy.md"]);
        Assert.Equal("# Demo\n", files.Files["README.md"]);
        Assert.Contains("backup/Worker.cs", files.Files.Keys);
    }

    [Fact]
    public async Task CopyFile_RefusesWhatCannotBeCopied()
    {
        await Assert.ThrowsAsync<IOException>(() => Run("copy_file", """{"from":"README.md","to":"docs/notes.md"}"""));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Run("copy_file", """{"from":"missing.txt","to":"x.txt"}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => Run("copy_file", """{"from":"README.md","to":""}"""));
    }

    [Fact]
    public void Create_WithMounts_NamesThemInTheDescriptionsThatNeedToKnow()
    {
        IReadOnlyList<PromptTool> tools = library.Create(files, ["docs", "data"], false);

        Assert.Contains("The mounted folders are: docs, data.", tools.Single(tool => tool.Name == "list_directory").Description);
        Assert.Contains("docs, data", tools.Single(tool => tool.Name == "copy_file").Description);
        Assert.Contains("docs, data", tools.Single(tool => tool.Name == "move_file").Description);
        Assert.DoesNotContain("The mounted folders are", library.Create(files, [], false).Single(tool => tool.Name == "list_directory").Description);
    }

    [Fact]
    public void Create_WithAWorkspace_SaysThatPathsAreRelativeToItExceptInsideTheMounts()
    {
        IReadOnlyList<PromptTool> tools = library.Create(files, ["my/docs"], true);

        string listing = tools.Single(tool => tool.Name == "list_directory").Description;
        Assert.Contains("relative to the workspace", listing);
        Assert.Contains("my/docs", listing);
        Assert.Contains("list the workspace", listing);
        Assert.DoesNotContain("Paths start with the path of a mounted folder", listing);
        Assert.Contains("relative to the workspace", tools.Single(tool => tool.Name == "move_file").Description);
        Assert.DoesNotContain("my/docs", library.Create(files, [], true).Single(tool => tool.Name == "list_directory").Description);
    }

    private async Task<string> RunMounted(FakeStore docs, string name, string json)
    {
        PromptTool tool = library.Create(new MountedStore(null, new Dictionary<string, IStore> { ["docs"] = docs, ["other"] = new FakeStore() }), ["docs", "other"], false).Single(candidate => candidate.Name == name);
        return await tool.Handler(JsonDocument.Parse(json).RootElement, CancellationToken.None);
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("docs/")]
    [InlineData("docs/.")]
    [InlineData("./docs")]
    [InlineData("docs//")]
    [InlineData("docs/x/..")]
    [InlineData("other/../docs")]
    public async Task MountedFolders_CannotBeDeletedOrRenamedOrOverwrittenUnderAnySpelling(string path)
    {
        FakeStore docs = new(new Dictionary<string, string> { ["guide.md"] = "guide" });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RunMounted(docs, "delete_file", $$"""{"path":"{{path}}"}"""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RunMounted(docs, "move_file", $$"""{"from":"{{path}}","to":"renamed"}"""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RunMounted(docs, "move_file", $$"""{"from":"{{path}}","to":"other/renamed"}"""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RunMounted(docs, "write_file", $$"""{"path":"{{path}}","content":"x"}"""));
        await Assert.ThrowsAsync<IOException>(() => RunMounted(docs, "copy_file", $$"""{"from":"docs/guide.md","to":"{{path}}"}"""));

        Assert.Equal(["guide.md"], docs.Files.Keys);
    }

    [Fact]
    public async Task MountedFolders_StillAllowEverythingInsideThem()
    {
        FakeStore docs = new(new Dictionary<string, string> { ["guide.md"] = "guide" });

        await RunMounted(docs, "write_file", """{"path":"docs/new.md","content":"x"}""");
        await RunMounted(docs, "move_file", """{"from":"docs/guide.md","to":"docs/manual.md"}""");
        await RunMounted(docs, "delete_file", """{"path":"docs/new.md"}""");

        Assert.Equal(["manual.md"], docs.Files.Keys);
    }

    [Fact]
    public async Task ReadFile_OfAFileOverTenMegabytes_IsRefused()
    {
        files.Files["huge.txt"] = new string('x', (10 * 1024 * 1024) + 1);
        files.Files["limit.txt"] = new string('y', 10 * 1024 * 1024);

        IOException exception = await Assert.ThrowsAsync<IOException>(() => Run("read_file", """{"path":"huge.txt"}"""));

        Assert.Equal("The file 'huge.txt' is larger than 10 MB.", exception.Message);
        Assert.StartsWith("1\tyyy", await Run("read_file", """{"path":"limit.txt","limit":1}"""));
    }

    [Fact]
    public async Task EditFile_OfAFileOverTenMegabytes_IsRefusedAndLeftAlone()
    {
        files.Files["huge.txt"] = new string('x', (10 * 1024 * 1024) + 1);

        await Assert.ThrowsAsync<IOException>(() => Run("edit_file", """{"path":"huge.txt","old_string":"x","new_string":"y","replace_all":true}"""));

        Assert.StartsWith("xxx", files.Files["huge.txt"]);
    }

    [Fact]
    public async Task SearchFiles_SkipsFilesOverTenMegabytes()
    {
        files.Files["huge.txt"] = "RetryLimit " + new string('x', 10 * 1024 * 1024);

        string result = await Run("search_files", """{"pattern":"RetryLimit"}""");

        Assert.DoesNotContain("huge.txt", result);
        Assert.Contains("src/Config.cs", result);
    }

    [Fact]
    public async Task WriteFile_AndEditFile_WriteUtf8WithoutAByteOrderMark()
    {
        await Run("write_file", """{"path":"u.txt","content":"héllo"}""");
        await Run("edit_file", """{"path":"u.txt","old_string":"é","new_string":"e"}""");

        Assert.Equal("hello", files.Files["u.txt"]);
    }
}
