namespace Markwardt.LiveAi;

/// <summary>
/// A engine's command line tool could not be started, typically because it is not installed or not on the <c>PATH</c>. Retrying does not help, so <see cref="PromptRetry" /> does not retry it.
/// </summary>
/// <param name="message">What went wrong.</param>
/// <param name="innerException">The error behind it, if any.</param>
public sealed class PromptToolStartException(string message, Exception? innerException = null) : PromptSessionException(message, innerException);
