namespace Markwardt.LiveAi;

/// <summary>
/// One thing that happens in a session, as reported on <see cref="IPromptSession" />: a <see cref="PromptMessageEvent" /> (and the <see cref="PromptDeltaEvent" />s that make it up), a <see cref="PromptToolCallEvent" /> and its <see cref="PromptToolResultEvent" />, a <see cref="PromptUsageEvent" />, the subscription's <see cref="PromptWindowsEvent" />, a <see cref="PromptInterruptedEvent" />, a <see cref="PromptFailureEvent" /> a <see cref="PromptStateEvent" /> when the session changes state or a <see cref="PromptDescriptorsEvent" /> when what it says about itself changes.
/// </summary>
public abstract record PromptEvent;
