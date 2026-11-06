namespace Markwardt.LiveAi;

/// <summary>
/// How the models of a server on this machine or the network are reached and run: the server, the model of each capability and the size of its context window. Every part is optional, so the same record serves to set them for a whole environment (<see cref="PromptEnvironment.LocalSettings" />) and to override only some of them for one session (<see cref="LocalSetup.Overrides" />): a part a session leaves out is the environment's, and a part neither sets is the default (<see cref="DefaultServer" /> for the server, the size the server picks for the context window), while the models must be set by one of them.
/// </summary>
public sealed record PromptServerSettings
{
    /// <summary>
    /// Gets the address of the OpenAI compatible API of Ollama on its usual port, which is used when neither the environment nor the setup of the session give another one.
    /// </summary>
    public static Uri DefaultServer { get; } = new("http://localhost:11434/v1");

    /// <summary>
    /// Gets the address of the server's OpenAI compatible API, including its version path, or <see langword="null" /> for <see cref="DefaultServer" />. Any server that speaks that API works, such as llama.cpp or LM Studio.
    /// </summary>
    public Uri? Server { get; init; }

    /// <summary>
    /// Gets the model that runs for each capability, as the server lists it.
    /// </summary>
    public PromptCapabilityModels? Models { get; init; }

    /// <summary>
    /// Gets the size of the context window for each capability, or <see langword="null" /> for the size the server runs the models with.
    /// </summary>
    public PromptCapabilityContexts? Contexts { get; init; }
}
