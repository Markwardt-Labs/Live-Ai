namespace Markwardt.LiveAi;

/// <summary>
/// How a request is run on a session, which decides where the AI answers it (see <see cref="PromptRequestOptions.Mode" />).
/// </summary>
public enum PromptRequestMode
{
    /// <summary>
    /// The request is asked in the main conversation, which is in the <see cref="PromptSessionState.Responding" /> state meanwhile, and its answer is the output of that work. It needs the session to be <see cref="PromptSessionState.Waiting" /> and throws a <see cref="PromptSessionStateException" /> otherwise.
    /// </summary>
    Direct,

    /// <summary>
    /// The request is mixed into the work the AI does anyway: it is given as input, in the main conversation, and the AI answers it by calling a <c>respond</c> tool with the unique identifier of the request, while its other output continues to flow to <see cref="IPromptSession.Output" />. It can be asked while the session is <see cref="PromptSessionState.Waiting" /> or <see cref="PromptSessionState.Working" />. The answer is a single message with what the AI gave to the tool, and nothing else about the work (its usage, tool calls and messages) is part of the request.
    /// </summary>
    Mixed,

    /// <summary>
    /// The request is asked in a copy of the conversation (a fork) in a process of its own, which is thrown away afterwards, so it does not touch the main conversation or its state. The fork starts from the conversation as the tool has stored it, which does not include what the AI is in the middle of doing. Any session state allows it.
    /// </summary>
    Fork
}
