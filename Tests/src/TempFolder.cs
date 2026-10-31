namespace Markwardt.LiveAi.Tests;

public sealed class TempFolder : IDisposable
{
    private readonly List<string> sessionFolders = [];

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Markwardt.LiveAi.Tests", Guid.NewGuid().ToString("N"));

    public TempFolder()
        => Directory.CreateDirectory(Path);

    public string Combine(string name)
        => System.IO.Path.Combine(Path, name);

    public async Task<string> ReadPrompt(string name = "claude-stdin.txt")
    {
        using JsonDocument frame = JsonDocument.Parse(await File.ReadAllTextAsync(Combine(name)));
        return frame.RootElement.GetProperty("message").GetProperty("content").GetString()!;
    }

    public string Script(string name, string body)
    {
        string path = Combine(name);
        File.WriteAllText(path, $"#!/bin/sh\n{body.Replace("$PWD", Path)}\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    public IPromptSession Track(IPromptSession session)
    {
        sessionFolders.Add(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"markwardt.liveai-{session.Id}"));
        return session;
    }

    public void Dispose()
    {
        foreach (string folder in sessionFolders.Where(Directory.Exists))
        {
            Directory.Delete(folder, true);
        }

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
