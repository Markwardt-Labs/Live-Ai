namespace Markwardt.LiveAi;

/// <summary>
/// A service that serves models through an OpenAI compatible HTTP API, such as Ollama, llama.cpp or LM Studio running on this machine. It is added to the configuration OpenCode is started with, so the models can be used without anyone editing OpenCode's own configuration files.
/// </summary>
public sealed record OpenCodeModelProvider
{
    /// <summary>
    /// Gets the identifier of the provider in OpenCode's configuration; a model is named <c>identifier/model</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the address of the API, including its version path, such as <c>http://localhost:11434/v1</c>.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Gets the models the provider serves.
    /// </summary>
    public required IReadOnlyList<OpenCodeModel> Models { get; init; }

    /// <summary>
    /// Gets the name to show for the provider, or <see langword="null" /> to use <see cref="Id" />.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the key the API is called with, or <see langword="null" /> for none or for the API key of the session.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Describes a local Ollama server.
    /// </summary>
    /// <param name="model">The Ollama name of the model, such as <c>gemma4:26b</c>.</param>
    /// <param name="baseUrl">The address of the server's OpenAI compatible API.</param>
    /// <param name="contextLimit">The size of the context window the server runs the model with, or <see langword="null" /> for OpenCode's default. Ollama decides this itself (see its <c>OLLAMA_CONTEXT_LENGTH</c> setting), so give the size it uses.</param>
    /// <returns>The provider, with the identifier <c>ollama</c>.</returns>
    public static OpenCodeModelProvider Ollama(string model, string baseUrl = "http://localhost:11434/v1", int? contextLimit = null)
        => new() { Id = "ollama", Name = "Ollama", BaseUrl = baseUrl, Models = [new OpenCodeModel { Id = model, ContextLimit = contextLimit }] };
}
