namespace Markwardt.LiveAi;

/// <summary>
/// The names of the three places besides the rest of the workspace that the files of a session (<see cref="IPromptSession.Workspace" />) have, which are the first part of a path in them, such as <c>.liveai-session/notes.txt</c>.
/// </summary>
public static class PromptStorageNames
{
    /// <summary>
    /// Gets the name of the session storage: it is kept in memory and in files the library manages, is empty when the session starts, and is lost when the session ends.
    /// </summary>
    public static string Session => ".liveai-session";

    /// <summary>
    /// Gets the name of the cache: like the session storage, except that a file in it that was not read or written for the retention of <see cref="PromptStorageSettings" /> is removed, and what the AI downloads goes there.
    /// </summary>
    public static string Cache => ".liveai-cache";

    /// <summary>
    /// Gets the name of the folder in the workspace itself, on disk, where the AI and the commands it runs put data that is wanted for a while: it is removed on the same terms as the cache, and when the session ends. Unlike the other two it is a real folder, so the shell reaches it.
    /// </summary>
    public static string Temp => ".liveai-temp";
}
