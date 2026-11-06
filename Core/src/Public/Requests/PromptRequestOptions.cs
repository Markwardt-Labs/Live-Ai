namespace Markwardt.LiveAi;

/// <summary>
/// How a request to a session is made, everything optional: what goes along with any input (the attachments and the note of <see cref="PromptSendOptions" />) and what only a request has. How the AI works (the model and its settings, the tools) belongs to the session and are set on its <see cref="IPromptSession.Configuration" />. Options are only read when the request starts, so one can be kept and used for many requests.
/// </summary>
public sealed record PromptRequestOptions : PromptSendOptions
{
    /// <summary>
    /// Gets the JSON Schema the AI must answer under, whose root must be an object, or <see langword="null" /> for an answer in plain text. The last message is then the answer, as JSON text. The typed extension methods of <see cref="IPromptSession" /> set it from a type.
    /// </summary>
    public JsonElement? Schema { get; init; }

    /// <summary>
    /// Gets where the AI answers the request: directly in the main conversation (the default), mixed into the work the AI is doing, or in a fork of the conversation. See <see cref="PromptRequestMode" /> for what each allows.
    /// </summary>
    public PromptRequestMode Mode { get; init; }
}
