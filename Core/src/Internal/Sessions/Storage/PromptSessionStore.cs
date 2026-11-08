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
    /// Reads every stored link, the most recently loaded first. A link that is unreadable is left out.
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

    /// <summary>
    /// Gets the full path of the folder the files of a session's session storage and cache move to when they do not fit in memory, which is inside the data folder and named after the session. The folder is not made. The first call also removes what sessions that ended without cleaning up left behind.
    /// </summary>
    /// <param name="id">The identifier of the session.</param>
    /// <returns>The path, or <see langword="null" /> when the files of the session stay in memory.</returns>
    string? GetStorageFolder(Guid id);
}

/// <inheritdoc />
/// <param name="folder">The folder that holds the stored sessions, or <see langword="null" /> for <c>Markwardt.LiveAi</c> in the local application data folder.</param>
internal sealed class PromptSessionStore(string? folder) : IPromptSessionStore
{
    private readonly string root = Path.GetFullPath(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Markwardt.LiveAi"));

    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter<PromptProvider>(JsonNamingPolicy.CamelCase), new JsonStringEnumConverter(), new CompactGuidConverter() } };

    private readonly IPromptSessionMigration migration = new PromptSessionMigration();

    private readonly object sweeping = new();

    private bool swept;

    private string Sessions => Path.Combine(root, "Sessions");

    /// <inheritdoc />
    public async Task Save(PromptSessionLink link, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(Sessions);
        await Write(PathOf(link.Id), JsonSerializer.Serialize(link with { Version = migration.Version }, json), cancellation);
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
            return await Read(path, cancellation);
        }
        catch (JsonException exception)
        {
            throw new PromptSessionException($"The stored session {id} is unreadable: {exception.Message}");
        }
        catch (PromptSessionException exception)
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
                if (await Read(path, cancellation) is { } link)
                {
                    links.Add(link);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or PromptSessionException)
            {
            }
        }

        return [.. links.OrderByDescending(link => link.LastLoaded).ThenBy(link => link.Id)];
    }

    /// <inheritdoc />
    public string GetWorkspaceFolder(Guid id)
        => Path.Combine(root, "Workspaces", id.ToString("N"));

    /// <inheritdoc />
    public string? GetStorageFolder(Guid id)
    {
        lock (sweeping)
        {
            if (!swept)
            {
                swept = true;
                StoragePool.ClearOrphans(root);
            }
        }

        return Path.Combine(root, id.ToString("N"));
    }

    /// <inheritdoc />
    public Task Delete(Guid id)
    {
        File.Delete(PathOf(id));
        StoragePool.TryClear(Path.Combine(root, id.ToString("N")));

        if (Directory.Exists(Sessions))
        {
            foreach (string backup in Directory.EnumerateFiles(Sessions, $"{id:N}.json.v*.bak"))
            {
                File.Delete(backup);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<PromptSessionLink?> Read(string path, CancellationToken cancellation)
    {
        string text = await File.ReadAllTextAsync(path, cancellation);

        if (JsonNode.Parse(text) is not JsonObject content)
        {
            return null;
        }

        int? from = migration.Upgrade(content);

        if (content["lastLoaded"] is null)
        {
            content["lastLoaded"] = JsonValue.Create(new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        }

        if (from is not null)
        {
            try
            {
                string backup = $"{path}.v{from}.bak";

                if (!File.Exists(backup))
                {
                    File.Copy(path, backup);

                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }

                await Write(path, content.ToJsonString(json), cancellation);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return content.Deserialize<PromptSessionLink>(json);
    }

    private async Task Write(string path, string text, CancellationToken cancellation)
    {
        string pending = $"{path}.{Guid.NewGuid():N}.tmp";

        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (StreamWriter writer = new(pending, options))
        {
            await writer.WriteAsync(text.AsMemory(), cancellation);
        }

        File.Move(pending, path, true);
    }

    private string PathOf(Guid id)
        => Path.Combine(Sessions, $"{id:N}.json");
}
