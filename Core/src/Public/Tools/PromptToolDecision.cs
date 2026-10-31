namespace Markwardt.LiveAi;

/// <summary>
/// What a hook decides about a <see cref="PromptToolCallEvent" /> before it runs.
/// </summary>
public sealed record PromptToolDecision
{
    /// <summary>
    /// Gets a decision to let the call run.
    /// </summary>
    public static PromptToolDecision Allow { get; } = new() { Allowed = true };

    /// <summary>
    /// Gets a value indicating whether the call may run.
    /// </summary>
    public required bool Allowed { get; init; }

    /// <summary>
    /// Gets why the call was refused, which the AI is told so that it can react, or <see langword="null" /> for no reason.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Makes a decision that refuses the call.
    /// </summary>
    /// <param name="reason">Why the call is refused, which the AI is told, or <see langword="null" /> for no reason.</param>
    /// <returns>The decision.</returns>
    public static PromptToolDecision Deny(string? reason = null)
        => new() { Allowed = false, Reason = reason };
}
