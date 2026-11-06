namespace Markwardt.LiveAi;

/// <summary>
/// A engine's command line tool could not be started, typically because it is not installed or not on the <c>PATH</c>.
/// </summary>
/// <param name="message">What went wrong.</param>
/// <param name="innerException">The error behind it, if any.</param>
public sealed class PromptToolStartException(string message, Exception? innerException = null) : PromptSessionException(message, innerException);
