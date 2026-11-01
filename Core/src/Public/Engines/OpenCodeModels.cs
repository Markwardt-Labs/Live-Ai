namespace Markwardt.LiveAi;

/// <summary>
/// Which models OpenCode uses for each <see cref="PromptCapability" />, written <c>provider/model</c>, and the <see cref="OpenCodeModelProvider" />s it needs to reach them. For the usual cases use <see cref="Ollama(string, string, int?)" /> (one local model for everything), <see cref="ByCapability(IEnumerable{ValueTuple{PromptCapability, string}}, string, int?)" /> (a model for each capability) or <see cref="Configured(string)" /> (one model that OpenCode's own configuration already has); for anything else set the properties, which is all that the factories do.
/// </summary>
public sealed record OpenCodeModels : PromptModels
{
    /// <summary>
    /// Makes the models for one model that OpenCode's own configuration already knows, such as one of a provider it is signed in to.
    /// </summary>
    /// <param name="model">The model, written <c>provider/model</c>, such as <c>anthropic/claude-sonnet</c>.</param>
    /// <returns>The models, which use it for every capability.</returns>
    /// <exception cref="ArgumentException"><paramref name="model" /> is empty.</exception>
    public static OpenCodeModels Configured(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new OpenCodeModels { Default = model };
    }

    /// <summary>
    /// Makes the models for one model that runs on this machine, served by Ollama.
    /// </summary>
    /// <param name="model">The Ollama name of the model, such as <c>gemma4:26b</c>.</param>
    /// <param name="baseUrl">The address of the server's OpenAI compatible API.</param>
    /// <param name="contextLimit">The size of the context window the server runs the model with, or <see langword="null" /> for OpenCode's default. Ollama decides this itself (see its <c>OLLAMA_CONTEXT_LENGTH</c> setting), so give the size it uses.</param>
    /// <returns>The models, which use it for every capability, with the provider <c>ollama</c> for it.</returns>
    public static OpenCodeModels Ollama(string model, string baseUrl = "http://localhost:11434/v1", int? contextLimit = null)
        => new() { Default = $"ollama/{model}", Providers = [OpenCodeModelProvider.Ollama(model, baseUrl, contextLimit)] };

    /// <summary>
    /// Makes the models for a choice of model for each capability that is given. A name that contains a <c>/</c> is a model of OpenCode's own configuration, written <c>provider/model</c>; any other name is a model of a local Ollama server, which is added as the provider <c>ollama</c>. A capability that is not given falls back to the model OpenCode is configured with; with no models at all, OpenCode's own configuration decides everything.
    /// </summary>
    /// <param name="models">The model of each capability, each capability at most once.</param>
    /// <param name="baseUrl">The address of the local server's OpenAI compatible API, for the models whose names have no <c>/</c>.</param>
    /// <param name="contextLimit">The size of the context window the server runs those models with, or <see langword="null" /> for OpenCode's default.</param>
    /// <returns>The models.</returns>
    /// <exception cref="ArgumentException">A name is empty, or a capability is given twice.</exception>
    public static OpenCodeModels ByCapability(IEnumerable<(PromptCapability Capability, string Model)> models, string baseUrl = "http://localhost:11434/v1", int? contextLimit = null)
    {
        Dictionary<PromptCapability, string> chosen = Choose(models);
        List<string> local = [.. chosen.Values.Where(model => !model.Contains('/')).Distinct()];
        string? Name(PromptCapability capability) => chosen.TryGetValue(capability, out string? model) ? model.Contains('/') ? model : $"ollama/{model}" : null;

        return new()
        {
            Fast = Name(PromptCapability.Fast),
            Balanced = Name(PromptCapability.Balanced),
            Max = Name(PromptCapability.Max),
            Providers = local.Count == 0 ? [] : [new OpenCodeModelProvider { Id = "ollama", Name = "Ollama", BaseUrl = baseUrl, Models = [.. local.Select(model => new OpenCodeModel { Id = model, ContextLimit = contextLimit })] }]
        };
    }

    /// <summary>
    /// Gets the model providers to add to OpenCode's configuration, besides the ones its own configuration files define. The models named in this record must be among their models, or among the models of OpenCode's own configuration.
    /// </summary>
    public IReadOnlyList<OpenCodeModelProvider> Providers { get; init; } = [];
}
