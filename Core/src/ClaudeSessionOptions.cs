namespace Markwardt.LiveAi;

/// <summary>
/// Options for a session that runs on Claude Code (<c>claude</c>).
/// </summary>
public sealed record ClaudeSessionOptions : PromptSessionOptions
{
    /// <summary>
    /// Gets the API key to use, or <see langword="null" /> for the subscription already signed in to in <c>claude</c>. The key is stored with the session, in plain text in its file in the data folder (readable by the current user only on Linux and macOS), so a session that is loaded runs on the same key.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the command that starts Claude Code: <c>claude</c> found on the <c>PATH</c>, or the path of another one.
    /// </summary>
    public string Command { get; init; } = "claude";

    /// <summary>
    /// Gets the folder Claude Code keeps its configuration and sessions in, or <see langword="null" /> for its usual one.
    /// </summary>
    public string? ConfigFolder { get; init; }

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
        builder.Append(", Command = ").Append(Command);
        builder.Append(", ConfigFolder = ").Append(ConfigFolder);
        return true;
    }
}
