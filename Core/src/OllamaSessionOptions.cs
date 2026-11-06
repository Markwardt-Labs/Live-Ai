namespace Markwardt.LiveAi;

/// <summary>
/// Options for a session that runs a model on this machine served by Ollama, driven through OpenCode (<c>opencode</c>).
/// </summary>
public sealed record OllamaSessionOptions : PromptSessionOptions
{
    /// <summary>
    /// Gets the address of the OpenAI compatible API of the Ollama server. It is Ollama's usual one by default.
    /// </summary>
    public Uri Server { get; init; } = new("http://localhost:11434/v1");

    /// <summary>
    /// Gets the model that runs for each capability of the session: one model for all of them (<see cref="PromptCapabilityModels.All" />) or a different one for each. A name is the one Ollama lists (<c>gemma4:26b</c>), and a session fails at its first prompt when the server does not offer it. It has no default, since which model to run is the one thing the library cannot know about a local server.
    /// </summary>
    public required PromptCapabilityModels Models { get; init; }

    /// <summary>
    /// Gets the size of the context window to run the model with, in tokens, or <see langword="null" /> for the size the server runs it with.
    /// </summary>
    public int? Context { get; init; }

    /// <summary>
    /// Gets the command that starts OpenCode: <c>opencode</c> found on the <c>PATH</c>, or the path of another one.
    /// </summary>
    public string Command { get; init; } = "opencode";
}
