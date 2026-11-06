namespace Markwardt.LiveAi.Tests;

/// <summary>
/// Makes the tool providers the tests work with, over an engine of their own choosing.
/// </summary>
internal static class TestProviders
{
    /// <summary>
    /// Makes a provider over an engine, with stored sessions in a folder.
    /// </summary>
    /// <param name="engine">The tool the provider drives.</param>
    /// <param name="dataFolder">The folder that holds the stored sessions, or <see langword="null" /> for the usual one.</param>
    /// <param name="logger">The logger the sessions report to, or <see langword="null" /> for none.</param>
    /// <param name="time">The clock the sessions run on, or <see langword="null" /> for the system's.</param>
    /// <returns>The provider.</returns>
    public static PromptProvider Make(IPromptEngine engine, string? dataFolder = null, ILogger? logger = null, TimeProvider? time = null)
    {
        TimeProvider clock = time ?? TimeProvider.System;
        IPromptSessionStore store = new PromptSessionStore(dataFolder);
        ITurnRunner runner = new TurnRunner();
        IPromptSessionOpener opener = new PromptSessionOpener(store, runner, new PromptToolHost(), new PromptLibraryTools(null, clock), logger, clock);
        return new PromptProvider(engine, store, opener, runner, clock);
    }

    /// <summary>
    /// Makes a prompter that runs every kind of options on one engine, with stored sessions in a folder.
    /// </summary>
    /// <param name="engine">The tool every session runs on.</param>
    /// <param name="dataFolder">The folder that holds the stored sessions.</param>
    /// <returns>The prompter.</returns>
    public static Prompter MakePrompter(IPromptEngine engine, string dataFolder)
        => MakePrompter(_ => engine, dataFolder);

    /// <summary>
    /// Makes a prompter that runs each kind of options on an engine of its own, with stored sessions in a folder.
    /// </summary>
    /// <param name="engines">Makes the engine for options.</param>
    /// <param name="dataFolder">The folder that holds the stored sessions.</param>
    /// <returns>The prompter.</returns>
    public static Prompter MakePrompter(Func<PromptSetup, IPromptEngine> engines, string dataFolder)
    {
        IPromptSessionStore store = new PromptSessionStore(dataFolder);
        ITurnRunner runner = new TurnRunner();
        IPromptSessionOpener opener = new PromptSessionOpener(store, runner, new PromptToolHost(), new PromptLibraryTools(null, TimeProvider.System), null, TimeProvider.System);
        return new Prompter(store, opener, runner, TimeProvider.System, engines);
    }
}
