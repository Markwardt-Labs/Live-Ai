namespace Markwardt.LiveAi;

/// <summary>
/// Turns a stored link into a running <see cref="IPromptSession" />.
/// </summary>
internal interface IPromptSessionOpener
{
    /// <summary>
    /// Opens a session.
    /// </summary>
    /// <param name="link">What is stored about the session.</param>
    /// <param name="provider">The provider that opens the session, which the session reports.</param>
    /// <param name="engine">The command line tool the session runs on.</param>
    /// <param name="apiKey">The API key to run with, or <see langword="null" /> to use the configured subscription.</param>
    /// <param name="model">The model the link names, as the tool lists it, or <see langword="null" /> to use only what the link says about it.</param>
    /// <returns>The session.</returns>
    IPromptSession Open(PromptSessionLink link, IPromptProvider provider, IPromptEngine engine, string? apiKey, PromptModel? model = null);
}

/// <inheritdoc />
/// <param name="store">Keeps the links.</param>
/// <param name="runner">Runs the engine's process.</param>
/// <param name="toolHost">Starts the servers that offer the caller's tools.</param>
/// <param name="libraryTools">Builds the library's file tools.</param>
/// <param name="logger">The caller's logger, or <see langword="null" /> for none.</param>
/// <param name="time">The clock the sessions run on, or <see langword="null" /> for the system's.</param>
internal sealed class PromptSessionOpener(IPromptSessionStore store, ITurnRunner runner, IPromptToolHost toolHost, IPromptLibraryTools libraryTools, ILogger? logger = null, TimeProvider? time = null) : IPromptSessionOpener
{
    /// <inheritdoc />
    public IPromptSession Open(PromptSessionLink link, IPromptProvider provider, IPromptEngine engine, string? apiKey, PromptModel? model = null)
    {
        PromptSessionRecord record = link.ToRecord(engine);
        PromptSession session = new(model is null ? record : record with { Model = model }, apiKey, store, provider, engine, runner, toolHost, libraryTools, this, logger, time);

        if (link.WorkspacePath is not null)
        {
            session.Configuration.Workspace = IStore.FromDisk(link.WorkspacePath, link.Sandbox);
        }

        return session;
    }
}
