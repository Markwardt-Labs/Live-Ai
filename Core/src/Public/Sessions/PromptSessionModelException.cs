namespace Markwardt.LiveAi;

/// <summary>
/// A session cannot do what was asked because of its model: none is selected, or the one it was stored with is no longer there.
/// </summary>
/// <param name="message">What is wrong.</param>
public sealed class PromptSessionModelException(string message) : PromptSessionException(message);
