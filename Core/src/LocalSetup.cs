namespace Markwardt.LiveAi;

/// <summary>
/// The setup of a session that runs a model served by a model server on this machine or the network (Ollama, LM Studio, llama.cpp or any other that speaks the OpenAI compatible API), driven through OpenCode. Where OpenCode is and which server and models are used are set for the whole environment (<see cref="PromptEnvironment" />); a session only overrides what it needs to differ in.
/// </summary>
public sealed record LocalSetup : PromptSetup
{
    /// <summary>
    /// Lists the models of a server that can be used for <see cref="PromptServerSettings.Models" />, by the name the server lists them with (<c>gemma4:26b</c>). Ollama is asked through its own API, so only the models that can call tools are listed (an embedding model, say, is left out); any other server is asked through the standard <c>models</c> endpoint, which lists names only, so all of its models are listed.
    /// </summary>
    /// <param name="server">The address of the server's OpenAI compatible API, or <see langword="null" /> for Ollama's usual one (<see cref="PromptServerSettings.DefaultServer" />).</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The names of the models, empty when the server cannot be reached or has none.</returns>
    public static async Task<IReadOnlyList<string>> ListModels(Uri? server = null, CancellationToken cancellation = default)
    {
        using HttpClient http = new();
        return [.. (await new LocalServer((server ?? PromptServerSettings.DefaultServer).ToString(), http).List(cancellation)).Select(model => model.Name ?? model.Id)];
    }

    /// <summary>
    /// Gets what this session sets differently from the environment's <see cref="PromptEnvironment.LocalSettings" />: the parts that are set replace the environment's, and the parts that are left out are the environment's. It is stored with the session. It is empty by default.
    /// </summary>
    public PromptServerSettings Overrides { get; init; } = new();
}
