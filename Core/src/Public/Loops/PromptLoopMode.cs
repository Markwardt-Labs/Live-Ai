namespace Markwardt.LiveAi;

/// <summary>
/// When a prompt loop sends its prompt again.
/// </summary>
public enum PromptLoopMode
{
    /// <summary>
    /// As soon as the last send is done.
    /// </summary>
    Continuous,

    /// <summary>
    /// When the last send is done and the interval has passed since it started.
    /// </summary>
    Interval,

    /// <summary>
    /// Never on its own: only when <see cref="IPromptLoop{TReply}.Trigger" /> is called.
    /// </summary>
    Manual
}
