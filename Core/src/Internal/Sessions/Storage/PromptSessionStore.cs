namespace Markwardt.LiveAi;

/// <summary>
/// Keeps <see cref="PromptSessionLink" />s so that sessions outlive the process that created them.
/// </summary>
internal interface IPromptSessionStore
{
    /// <summary>
    /// Writes a link, replacing any stored under the same identifier.
    /// </summary>
    /// <param name="link">The link to store.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>A task that completes once the link is stored.</returns>
    Task Save(PromptSessionLink link, CancellationToken cancellation = default);

    /// <summary>
    /// Reads the link stored under an identifier.
    /// </summary>
    /// <param name="id">The identifier the library gave the session.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The link, or <see langword="null" /> when none is stored.</returns>
    /// <exception cref="PromptSessionException">The stored link is unreadable.</exception>
    Task<PromptSessionLink?> Find(Guid id, CancellationToken cancellation = default);

    /// <summary>
    /// Reads every stored link, newest first (links without a creation time last). A link that is unreadable is left out.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The links.</returns>
    Task<IReadOnlyList<PromptSessionLink>> List(CancellationToken cancellation = default);

    /// <summary>
    /// Deletes the link stored under an identifier, if any.
    /// </summary>
    /// <param name="id">The identifier the library gave the session.</param>
    /// <returns>A task that completes once the link is gone.</returns>
    Task Delete(Guid id);

    /// <summary>
    /// Gets the full path of the folder a session works in when its caller gave it none, which is inside the data folder and named after the session. The folder is not made.
    /// </summary>
    /// <param name="id">The identifier of the session.</param>
    /// <returns>The path.</returns>
    string GetWorkspaceFolder(Guid id);
}

/// <inheritdoc />
/// <param name="folder">The folder that holds the stored sessions, or <see langword="null" /> for <c>Markwardt.LiveAi</c> in the local application data folder.</param>
internal sealed class PromptSessionStore(string? folder) : IPromptSessionStore
{
    private readonly string root = Path.GetFullPath(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Markwardt.LiveAi"));

    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(), new CompactGuidConverter() } };

    private string Sessions => Path.Combine(root, "Sessions");

    /// <inheritdoc />
    public async Task Save(PromptSessionLink link, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(Sessions);
        string path = PathOf(link.Id);
        string pending = $"{path}.{Guid.NewGuid():N}.tmp";

        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (StreamWriter writer = new(pending, options))
        {
            await writer.WriteAsync(JsonSerializer.Serialize(link, json).AsMemory(), cancellation);
        }

        File.Move(pending, path, true);
    }

    /// <inheritdoc />
    public async Task<PromptSessionLink?> Find(Guid id, CancellationToken cancellation = default)
    {
        string path = PathOf(id);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PromptSessionLink>(await File.ReadAllTextAsync(path, cancellation), json);
        }
        catch (JsonException exception)
        {
            throw new PromptSessionException($"The stored session {id} is unreadable: {exception.Message}");
        }
    }


    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptSessionLink>> List(CancellationToken cancellation = default)
    {
        List<PromptSessionLink> links = [];

        if (!Directory.Exists(Sessions))
        {
            return links;
        }

        foreach (string path in Directory.EnumerateFiles(Sessions, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<PromptSessionLink>(await File.ReadAllTextAsync(path, cancellation), json) is { } link)
                {
                    links.Add(link);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
            }
        }

        return [.. links.OrderByDescending(link => link.Created ?? DateTimeOffset.MinValue).ThenBy(link => link.Id)];
    }

    /// <inheritdoc />
    public string GetWorkspaceFolder(Guid id)
        => Path.Combine(root, "Workspaces", id.ToString("N"));

    /// <inheritdoc />
    public Task Delete(Guid id)
    {
        File.Delete(PathOf(id));
        return Task.CompletedTask;
    }

    private string PathOf(Guid id)
        => Path.Combine(Sessions, $"{id:N}.json");
}
