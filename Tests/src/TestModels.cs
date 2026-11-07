namespace Markwardt.LiveAi.Tests;

/// <summary>
/// The model the tests run on, so that a session can be used: one that offers an effort and a toggle.
/// </summary>
internal static class TestModels
{
    /// <summary>
    /// Gets a time for the stored sessions the tests make by hand, which is neither now nor the beginning of time.
    /// </summary>
    public static DateTimeOffset Loaded { get; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Gets a model with the settings <c>effort</c> (<c>low</c>, <c>medium</c>, <c>high</c>, medium by default) and <c>fast</c> (a toggle, off by default).
    /// </summary>
    public static PromptModel Model { get; } = new()
    {
        Id = "test-model",
        Name = "Test model",
        Settings = [PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"]), PromptModelSetting.Switch("fast", "Fast")]
    };

    /// <summary>
    /// Gets tool service options with nothing set.
    /// </summary>
    public static PromptToolServiceOptions Options { get; } = new();

    /// <summary>
    /// Makes tool service options as given.
    /// </summary>
    /// <param name="apiKey">The API key.</param>
    /// <param name="workspacePath">The workspace path.</param>
    /// <returns>The options.</returns>
    public static PromptToolServiceOptions With(string? apiKey = null, string? workspacePath = null)
        => new() { ApiKey = apiKey, WorkspacePath = workspacePath };
}
