namespace Markwardt.LiveAi;

/// <summary>
/// Image generation with the OpenAI image API. The model is chosen from the capability among the image models the key can use (the cheapest for <see cref="PromptCapability.Fast" />, the everyday one for <see cref="PromptCapability.Balanced" />, and the most capable for <see cref="PromptCapability.Strong" /> and <see cref="PromptCapability.Max" />, since OpenAI has no tier between), the first time an image is made, and every model it can pick makes images with a transparent background.
/// </summary>
public sealed record OpenAiImageOptions : PromptImageOptions
{
    /// <summary>
    /// Gets the OpenAI API key, which is needed to make an image. It is only sent to OpenAI and is never logged, and an error message the service gives leaves it out. It is stored with the session, in plain text in its file in the data folder (readable by the current user only on Linux and macOS), so a session that is loaded can make images with it.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets how capable the model is, <see cref="PromptCapability.Balanced" /> by default. A more capable model costs more per image.
    /// </summary>
    public PromptCapability Capability { get; init; } = PromptCapability.Balanced;

    /// <summary>
    /// Describes the members for <see cref="object.ToString" />, with the API key hidden, so it does not end up in a log or a failure message.
    /// </summary>
    /// <param name="builder">Receives the text.</param>
    /// <returns><see langword="true" />, since members were written.</returns>
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ApiKey = ").Append(ApiKey is null ? string.Empty : "***");
        builder.Append(", Capability = ").Append(Capability);
        return true;
    }
}
