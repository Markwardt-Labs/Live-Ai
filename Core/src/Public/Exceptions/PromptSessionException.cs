namespace Markwardt.LiveAi;

/// <summary>
/// An AI session could not do what was asked of it.
/// </summary>
/// <param name="message">What went wrong.</param>
/// <param name="innerException">The error behind it, if any.</param>
public class PromptSessionException(string message, Exception? innerException = null) : Exception(message, innerException);
