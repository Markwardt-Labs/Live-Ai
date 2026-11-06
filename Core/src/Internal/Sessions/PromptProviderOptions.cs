namespace Markwardt.LiveAi;

/// <summary>
/// What a tool provider creates a session with.
/// </summary>
internal sealed record PromptProviderOptions
{
    /// <summary>
    /// Gets the API key, or <see langword="null" /> for the sign in already configured in the tool.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the setup the session is created with, which is stored with it so that it can be loaded by its identifier alone, or <see langword="null" /> when there are none to store.
    /// </summary>
    public PromptSetup? Setup { get; init; }

    /// <summary>
    /// Gets the folder the session works in, or <see langword="null" /> for none.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Describes the members for <see cref="object.ToString" />, with the API key hidden, so it does not end up in a log or a failure message.
    /// </summary>
    /// <param name="builder">Receives the text.</param>
    /// <returns><see langword="true" />, since members were written.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ApiKey = ").Append(ApiKey is null ? string.Empty : "***");
        builder.Append(", Setup = ").Append(Setup);
        builder.Append(", WorkspacePath = ").Append(WorkspacePath);
        return true;
    }
}
