namespace Markwardt.LiveAi.Tests;

/// <summary>
/// The model the tests run on, so that a session can be used: one that offers an effort and a toggle.
/// </summary>
internal static class TestModels
{
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
    /// Gets provider options with nothing set.
    /// </summary>
    public static PromptProviderOptions Options { get; } = new();

    /// <summary>
    /// Makes provider options as given.
    /// </summary>
    /// <param name="apiKey">The API key.</param>
    /// <param name="workspacePath">The workspace path.</param>
    /// <returns>The options.</returns>
    public static PromptProviderOptions With(string? apiKey = null, string? workspacePath = null)
        => new() { ApiKey = apiKey, WorkspacePath = workspacePath };
}
