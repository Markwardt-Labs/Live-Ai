namespace Markwardt.LiveAi.Tests.Integration;

public sealed class EngineSessionTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose()
        => temp.Dispose();

    private void Touch(string relative)
    {
        string path = temp.Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
    }

    [Fact]
    public void Claude_FindsASessionInAnyProjectFolder()
    {
        Touch("projects/-some-project/abc.jsonl");
        ClaudeEngine engine = new("claude", temp.Path);

        Assert.True(engine.HasSession("abc"));
        Assert.False(engine.HasSession("other"));
    }

    [Fact]
    public void Claude_WhenNothingIsStored_HasNoSession()
        => Assert.False(new ClaudeEngine("claude", temp.Combine("missing")).HasSession("abc"));

    [Fact]
    public void Codex_FindsASessionByItsThreadInAnyDateFolder()
    {
        Touch("sessions/2026/08/16/rollout-2026-08-16T02-44-10-abc.jsonl");
        CodexEngine engine = new("codex", temp.Path);

        Assert.True(engine.HasSession("abc"));
        Assert.False(engine.HasSession("bc"));
        Assert.False(engine.HasSession("other"));
    }

    [Fact]
    public void Codex_WhenNothingIsStored_HasNoSession()
        => Assert.False(new CodexEngine("codex", temp.Combine("missing")).HasSession("abc"));
}
