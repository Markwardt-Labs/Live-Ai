namespace Markwardt.LiveAi;

/// <summary>
/// Image generation with the OpenAI image API. Only the key is set here: which image model makes an image is for the AI to choose through the capability argument of its tool (the cheapest for <see cref="PromptCapability.Fast" />, the everyday one for <see cref="PromptCapability.Balanced" />, and the most capable for <see cref="PromptCapability.Strong" /> and <see cref="PromptCapability.Max" />, since OpenAI has no tier between), from the image models the key can use.
/// </summary>
public sealed record OpenAiImageSetup : PromptImageSetup
{
    /// <summary>
    /// Gets the OpenAI API key, which is needed to make an image. It is only sent to OpenAI and is never logged, and an error message the service gives leaves it out. It lives in the environment (<see cref="PromptEnvironment.ImageGeneration" />) and is not stored with the sessions that use it.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Describes the members for <see cref="object.ToString" />, with the API key hidden, so it does not end up in a log or a failure message.
    /// </summary>
    /// <param name="builder">Receives the text.</param>
    /// <returns><see langword="true" />, since members were written.</returns>
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ApiKey = ").Append(ApiKey is null ? string.Empty : "***");
        return true;
    }
}
