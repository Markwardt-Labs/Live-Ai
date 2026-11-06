namespace Markwardt.LiveAi;

/// <summary>
/// A service that serves models through an OpenAI compatible HTTP API, such as Ollama, llama.cpp or LM Studio running on this machine. It is added to the configuration OpenCode is started with, so the models can be used without anyone editing OpenCode's own configuration files.
/// </summary>
internal sealed record OpenCodeModelProvider
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
    /// Describes the members for <see cref="object.ToString" />, with the API key hidden, so it does not end up in a log or a failure message.
    /// </summary>
    /// <param name="builder">Receives the text.</param>
    /// <returns><see langword="true" />, since members were written.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id);
        builder.Append(", Name = ").Append(Name);
        builder.Append(", BaseUrl = ").Append(BaseUrl);
        builder.Append(", Models = ").Append(string.Join(", ", Models.Select(model => model.Id)));
        builder.Append(", ").Append("ApiKey = ").Append(ApiKey is null ? string.Empty : "***");
        return true;
    }
}
