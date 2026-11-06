namespace Markwardt.LiveAi;

/// <summary>
/// The AI's answer to a prompt that asked for structured output could not be used: there was none, or it is not JSON of the requested type.
/// </summary>
public sealed class PromptSessionOutputException : PromptSessionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PromptSessionOutputException" /> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="output">The last message the AI wrote, or <see langword="null" /> when it wrote none.</param>
    /// <param name="innerException">The JSON error behind it, if any.</param>
    public PromptSessionOutputException(string message, string? output, Exception? innerException = null)
        : base(message, innerException) => Output = output;

    /// <summary>
    /// Gets the last message the AI wrote, or <see langword="null" /> when it wrote none.
    /// </summary>
    public string? Output { get; }
}
