namespace Markwardt.LiveAi.Testing;

/// <summary>
/// A prompt that a <see cref="FakePromptSession" /> was given, as the real session would have run it.
/// </summary>
public sealed record FakePrompt
{
    private readonly List<string> steering = [];

    private readonly List<string> steeringNotes = [];

    /// <summary>
    /// Gets when the prompt started.
    /// </summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>
    /// Gets the prompt text, with the paragraphs added to the request joined.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the input the session was given with <see cref="IPromptSession.Send(object, PromptOptions?, CancellationToken)" /> or a mixed request while the prompt ran, which the AI took into its work at once.
    /// </summary>
    public IReadOnlyList<string> Steered
    {
        get
        {
            lock (steering)
            {
                return [.. steering];
            }
        }
    }

    /// <summary>
    /// Gets the notes that came with the input in <see cref="Steered" />.
    /// </summary>
    public IReadOnlyList<string> SteeredNotes
    {
        get
        {
            lock (steering)
            {
                return [.. steeringNotes];
            }
        }
    }

    /// <summary>
    /// Gets the files of the prompt, read when it was asked.
    /// </summary>
    public IReadOnlyList<PromptAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Gets the schema the answer had to follow, or <see langword="null" /> for plain text.
    /// </summary>
    public JsonElement? Schema { get; init; }

    /// <summary>
    /// Gets the model the prompt ran with: the session's at the time.
    /// </summary>
    public required PromptModel Model { get; init; }

    /// <summary>
    /// Gets the values of the model's settings the prompt ran with: the session's at the time.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelSettings { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the notes the session was told (with <see cref="IPromptSession.Send(object, PromptOptions?, CancellationToken)" />) since the prompt before this one, which are delivered with it.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    internal void Take(string text, IReadOnlyList<string> told)
    {
        lock (steering)
        {
            steering.Add(text);

            steeringNotes.AddRange(told);
        }
    }
}
