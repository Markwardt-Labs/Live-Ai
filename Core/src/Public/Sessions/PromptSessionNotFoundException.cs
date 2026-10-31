namespace Markwardt.LiveAi;

/// <summary>
/// No session can be loaded under the requested identifier: none is stored, or the provider no longer has the session it was linked to.
/// </summary>
public sealed class PromptSessionNotFoundException : PromptSessionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PromptSessionNotFoundException" /> class for an identifier with nothing stored.
    /// </summary>
    /// <param name="id">The identifier that was asked for.</param>
    public PromptSessionNotFoundException(string id)
        : this(id, $"No session is stored under {id}.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptSessionNotFoundException" /> class.
    /// </summary>
    /// <param name="id">The identifier that was asked for.</param>
    /// <param name="message">What went wrong.</param>
    public PromptSessionNotFoundException(string id, string message)
        : base(message) => Id = id;

    /// <summary>
    /// Gets the identifier that was asked for.
    /// </summary>
    public string Id { get; }
}
