namespace Markwardt.LiveAi;

/// <summary>
/// The prompt was interrupted with <see cref="IPromptSession.Interrupt" />, so the AI stopped before it was done. It is the last event, and the prompt ends without an error, with what the AI had written until then.
/// </summary>
public sealed record PromptInterruptedEvent : PromptEvent;
