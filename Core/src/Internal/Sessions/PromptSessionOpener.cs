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
    /// <param name="service">The tool service that opens the session.</param>
    /// <param name="engine">The command line tool the session runs on.</param>
    /// <param name="apiKey">The API key to run with, or <see langword="null" /> to use the configured subscription.</param>
    /// <returns>The session.</returns>
    IPromptSession Open(PromptSessionLink link, IPromptToolService service, IPromptEngine engine, string? apiKey);
}

/// <inheritdoc />
/// <param name="store">Keeps the links.</param>
/// <param name="runner">Runs the engine's process.</param>
/// <param name="toolHost">Starts the servers that offer the caller's tools.</param>
/// <param name="libraryTools">Builds the library's file tools.</param>
/// <param name="logger">The caller's logger, or <see langword="null" /> for none.</param>
/// <param name="time">The clock the sessions run on, or <see langword="null" /> for the system's.</param>
/// <param name="images">Gives the image generation setup of the environment as it is when a prompt starts, or <see langword="null" /> for an environment without one.</param>
internal sealed class PromptSessionOpener(IPromptSessionStore store, ITurnRunner runner, IPromptToolHost toolHost, IPromptLibraryTools libraryTools, ILogger? logger = null, TimeProvider? time = null, Func<PromptImageSetup?>? images = null) : IPromptSessionOpener
{
    /// <inheritdoc />
    public IPromptSession Open(PromptSessionLink link, IPromptToolService service, IPromptEngine engine, string? apiKey)
    {
        if (link.WorkspacePath is not null && !Directory.Exists(link.WorkspacePath))
        {
            throw new DirectoryNotFoundException($"The workspace folder '{link.WorkspacePath}' does not exist.");
        }

        PromptSessionRecord record = link.ToRecord(engine);
        return new PromptSession(record, apiKey, store, service, engine, runner, toolHost, libraryTools, this, logger, time, null, images);
    }
}
