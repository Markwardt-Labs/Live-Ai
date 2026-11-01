namespace Markwardt.LiveAi;

/// <summary>
/// Lists the models Codex offers to the account it is signed in with, from <c>codex debug models</c>. Models Codex hides, which are for its own use, are left out.
/// </summary>
/// <param name="command">The command that starts Codex.</param>
/// <param name="configFolder">The folder Codex keeps its state in, or <see langword="null" /> for <c>CODEX_HOME</c> or <c>.codex</c> in the user's home folder.</param>
public sealed class CodexModelCatalog(string command = "codex", string? configFolder = null) : IPromptModelCatalog
{
    private readonly ITurnRunner turns = new TurnRunner();

    /// <summary>
    /// Gets how long the listing waits for Codex before it gives an empty list.
    /// </summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptModel>> List(CancellationToken cancellation = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(Timeout);

        try
        {
            ProcessStartInfo start = new(command);
            start.ArgumentList.Add("debug");
            start.ArgumentList.Add("models");

            if (configFolder is not null)
            {
                start.Environment["CODEX_HOME"] = configFolder;
            }

            List<string> lines = [];

            await foreach (string line in turns.Exchange("Codex", start, [], true, limit.Token))
            {
                lines.Add(line);
            }

            using JsonDocument catalog = JsonDocument.Parse(string.Join('\n', lines));
            return [.. catalog.RootElement.GetProperty("models").EnumerateArray().Where(Visible).Select(Read)];
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception exception) when (exception is PromptSessionException or JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        {
            return [];
        }
    }

    private bool Visible(JsonElement model)
        => model.TryGetProperty("visibility", out JsonElement visibility) && visibility.GetString() == "list";

    private PromptModel Read(JsonElement model)
        => new()
        {
            Id = model.GetProperty("slug").GetString() ?? throw new InvalidDataException("A model has no name."),
            Name = model.TryGetProperty("display_name", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            ContextLimit = model.TryGetProperty("context_window", out JsonElement context) && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out int limit) ? limit : null,
            Efforts = model.TryGetProperty("supported_reasoning_levels", out JsonElement levels) && levels.ValueKind == JsonValueKind.Array ? [.. levels.EnumerateArray().Where(level => level.TryGetProperty("effort", out _)).Select(level => level.GetProperty("effort").GetString()).OfType<string>()] : null
        };
}
