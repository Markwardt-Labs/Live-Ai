namespace Markwardt.LiveAi;

/// <summary>
/// A function of the caller's that the AI can call while it works on a prompt.
/// </summary>
public sealed record PromptTool
{
    /// <summary>
    /// Creates a tool from typed input and output: the input schema is built from <typeparamref name="TInput" />, the arguments are read into it, and the output is written back as JSON text (a string is returned as it is).
    /// </summary>
    /// <typeparam name="TInput">The type of the arguments; it must be written as a JSON object.</typeparam>
    /// <typeparam name="TOutput">The type of the result.</typeparam>
    /// <param name="name">The name the AI calls the tool by.</param>
    /// <param name="description">What the tool does and when to use it.</param>
    /// <param name="handler">Runs the tool.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TInput" /> is not written as a JSON object.</exception>
    [RequiresUnreferencedCode("The type is read and written by reflection, which trimming can remove members of. Keep the members of the type, or build the tool with your own schema and handler.")]
    [RequiresDynamicCode("The type is turned into a JSON schema and read by runtime code generation, which native AOT does not support.")]
    public static PromptTool Create<TInput, TOutput>(string name, string description, Func<TInput, CancellationToken, Task<TOutput>> handler)
        => new()
        {
            Name = name,
            Description = description,
            InputSchema = PromptJson.Schema(typeof(TInput)),
            Handler = async (arguments, cancellation) =>
        {
            TInput input = JsonSerializer.Deserialize<TInput>(arguments.GetRawText(), PromptJson.Options) ?? throw new JsonException("The arguments are null.");
            TOutput output = await handler(input, cancellation);
            return output is string text ? text : JsonSerializer.Serialize(output, PromptJson.Options);
        }
        };

    /// <summary>
    /// Gets the name the AI calls the tool by: letters, digits, underscores and hyphens, at most 64 characters, unique among a session's tools.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets what the tool does and when to use it, which is all the AI knows about it besides its input.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the JSON Schema of the tool's input, whose root must be an object.
    /// </summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>
    /// Gets the function that runs the tool, from the arguments the AI gave (an object that is only valid during the call) to the text it gets back. An exception becomes an error result that the AI sees, so it can react.
    /// </summary>
    public required Func<JsonElement, CancellationToken, Task<string>> Handler { get; init; }

}
