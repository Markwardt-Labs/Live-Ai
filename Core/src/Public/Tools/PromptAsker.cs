namespace Markwardt.LiveAi;

/// <summary>
/// Answers a question the AI puts to the user in the middle of its work (see <see cref="IPromptSessionTransientConfiguration.Asker" />).
/// </summary>
/// <param name="question">What the AI asks.</param>
/// <param name="cancellation">Cancels the wait, for example when the session is disposed.</param>
/// <returns>The reply, which the AI reads as the user's answer; <see langword="null" /> counts as an empty reply.</returns>
public delegate Task<string> PromptAsker(string question, CancellationToken cancellation = default);
