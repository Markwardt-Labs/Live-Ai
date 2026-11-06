namespace Markwardt.LiveAi;

/// <summary>
/// The setup of a session that runs on OpenAI Codex (<c>codex</c>).
/// </summary>
public sealed record CodexSetup : PromptSetup
{
    /// <summary>
    /// Gets the API key to use, or <see langword="null" /> for the key of the environment (<see cref="PromptEnvironment.CodexApiKey" />) and, when that is not set either, the sign in already configured in <c>codex</c>. The key the session is created with, whichever it is, is stored with the session, in plain text in its file in the data folder (readable by the current user only on Linux and macOS), so a session that is loaded runs on the same key.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Describes the members for <see cref="object.ToString" />, with the API key hidden, so it does not end up in a log or a failure message.
    /// </summary>
    /// <param name="builder">Receives the text.</param>
    /// <returns><see langword="true" />, since members were written.</returns>
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", ");
        builder.Append("ApiKey = ").Append(ApiKey is null ? string.Empty : "***");
        return true;
    }
}
