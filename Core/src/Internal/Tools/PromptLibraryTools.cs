namespace Markwardt.LiveAi;

/// <summary>
/// Builds the tools that let the AI work on an <see cref="IStore" />.
/// </summary>
internal interface IPromptLibraryTools
{
    /// <summary>
    /// Gets the names of the file tools, which a caller's own tools cannot use.
    /// </summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Gets the name of the shell tool, which a caller's own tools cannot use either.
    /// </summary>
    string ShellName { get; }

    /// <summary>
    /// Gets the name of the tool that asks the user a question, which a caller's own tools cannot use either.
    /// </summary>
    string AskName { get; }

    /// <summary>
    /// Builds the tools for a store.
    /// </summary>
    /// <param name="store">The files the tools work on, with the mounted folders at its top level.</param>
    /// <param name="mounts">The paths of the mounted folders, which the descriptions point out.</param>
    /// <param name="hasWorkspace">Whether relative paths outside the mounts are in a workspace, which the descriptions say.</param>
    /// <returns>The tools.</returns>
    IReadOnlyList<PromptTool> Create(IStore store, IReadOnlyList<string> mounts, bool hasWorkspace);

    /// <summary>
    /// Builds the tool that puts a question to the user.
    /// </summary>
    /// <param name="ask">Gets the user's reply to a question.</param>
    /// <returns>The tool.</returns>
    PromptTool CreateAsk(Func<string, CancellationToken, Task<string>> ask);

    /// <summary>
    /// Builds the tool that runs shell commands on this machine.
    /// </summary>
    /// <param name="workingFolder">The folder commands start in unless the AI names another.</param>
    /// <returns>The tool.</returns>
    PromptTool CreateShell(string workingFolder);
}

/// <inheritdoc />
internal sealed class PromptLibraryTools : IPromptLibraryTools
{
    private readonly int readLines = 2000;

    private readonly int lineLength = 2000;

    private readonly int matchLimit = 200;

    private readonly int findLimit = 1000;

    private readonly int readLimit = 10 * 1024 * 1024;

    private readonly TimeSpan searchTimeout = TimeSpan.FromSeconds(5);

    private readonly int shellOutputLimit = 64 * 1024;

    private readonly int shellDefaultSeconds = 120;

    private readonly int shellMaxSeconds = 3600;

    /// <summary>
    /// Gets how long a command's output is read after the command itself ended, for a background process that keeps the pipes open: it is waited for no longer than this.
    /// </summary>
    public TimeSpan ShellPipeGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public IReadOnlyList<string> Names { get; } = ["list_directory", "find_files", "search_files", "read_file", "write_file", "edit_file", "delete_file", "move_file", "copy_file"];

    /// <inheritdoc />
    public string ShellName => "shell";

    /// <inheritdoc />
    public string AskName => "ask_user";

    /// <inheritdoc />
    public IReadOnlyList<PromptTool> Create(IStore store, IReadOnlyList<string> mounts, bool hasWorkspace)
        => [
            PromptTool.Create<DirectoryInput, string>("list_directory", "Lists the files and folders directly in a folder (folders end with /). " + Where(mounts, hasWorkspace) + " Leave path out to list " + (hasWorkspace ? "the workspace." : "the mounted folders themselves."), (input, cancellation) => ListDirectory(store, input, cancellation)),
            PromptTool.Create<FindInput, string>("find_files", "Finds files by glob pattern, as paths you can give the other tools. A pattern without / (like *.cs) matches file names at any depth; with / (like src/**/*.md) it matches the whole path. * matches within a folder name, ** across folders, ? one character.", (input, cancellation) => FindFiles(store, input, cancellation)),
            PromptTool.Create<SearchInput, string>("search_files", "Searches file contents with a regular expression and returns path:line:text for each match (at most 200). Optionally limit it to a folder (path) or to files matching a glob (glob)." + Where(mounts, hasWorkspace), (input, cancellation) => SearchFiles(store, input, cancellation)),
            PromptTool.Create<ReadInput, string>("read_file", "Reads a text file and returns its lines numbered (line number, tab, text). Reads up to 2000 lines; use offset (first line, starting at 1) and limit to read more or a part.", (input, cancellation) => ReadFile(store, input, cancellation)),
            PromptTool.Create<WriteInput, string>("write_file", "Creates a file, or replaces the whole content of an existing one, with the given text. Missing folders are created. To change part of a file, use edit_file.", (input, cancellation) => WriteFile(store, input, cancellation)),
            PromptTool.Create<EditInput, string>("edit_file", "Replaces text in a file. old_string must match the file exactly (including whitespace) and, unless replace_all is true, exactly once: include enough surrounding text to make it unique. replace_all replaces every occurrence, for example to rename something.", (input, cancellation) => EditFile(store, input, cancellation)),
            PromptTool.Create<PathInput, string>("delete_file", "Deletes a file, or a folder with everything in it.", (input, cancellation) => DeleteFile(store, input, cancellation)),
            PromptTool.Create<MoveInput, string>("move_file", "Moves or renames a file, or a folder with everything in it. The destination must not exist." + Where(mounts, hasWorkspace), (input, cancellation) => MoveFile(store, input, cancellation)),
            PromptTool.Create<MoveInput, string>("copy_file", "Copies a file, or a folder with everything in it, to a new place. The destination must not exist." + Where(mounts, hasWorkspace), (input, cancellation) => CopyFile(store, input, cancellation))
        ];

    /// <inheritdoc />
    public PromptTool CreateAsk(Func<string, CancellationToken, Task<string>> ask)
        => PromptTool.Create<AskInput, string>(AskName, "Asks the user a question and returns their reply. Use it when you need information, a choice or a confirmation that only the user can give and you cannot find out yourself. Ask one clear, self-contained question at a time; the user cannot see your other messages. The call waits until the user has answered.", async (input, cancellation) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Question, nameof(input));
            return await ask(input.Question, cancellation) ?? string.Empty;
        });

    /// <inheritdoc />
    public PromptTool CreateShell(string workingFolder)
        => PromptTool.Create<ShellInput, string>(ShellName, $"Runs a shell command on the local machine, with the full rights of the user this program runs as, and returns its output (standard output and standard error together) followed by its exit code. The command runs with /bin/sh -c (cmd /c on Windows). It starts in an empty scratch folder unless working_directory is given, so use absolute paths or working_directory. Standard input is empty, so a command that waits for input ends at once. A command is stopped after timeout_seconds (default {shellDefaultSeconds}, at most {shellMaxSeconds}), and very long output is cut.", (input, cancellation) => RunShell(workingFolder, input, cancellation));

    private async Task<string> RunShell(string workingFolder, ShellInput input, CancellationToken cancellation = default)
    {
        int seconds = Math.Clamp(input.TimeoutSeconds ?? shellDefaultSeconds, 1, shellMaxSeconds);

        ProcessStartInfo start = new(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            WorkingDirectory = input.WorkingDirectory is { Length: > 0 } ? input.WorkingDirectory : workingFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(input.Command);

        using Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"The command could not be started: {exception.Message}");
        }

        process.StandardInput.Close();
        ShellOutput output = new(shellOutputLimit);
        Task readers = Task.WhenAll(Pump(process.StandardOutput, output), Pump(process.StandardError, output));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        bool timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Stop(process);

            if (cancellation.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        await Task.WhenAny(readers, Task.Delay(ShellPipeGrace, CancellationToken.None));
        string text = output.ToString().TrimEnd('\r', '\n');
        string status = timedOut ? $"(timed out after {seconds} seconds and stopped)" : $"(exit code {process.ExitCode})";
        string cut = output.IsCut ? "\n(output cut at 64 KB)" : string.Empty;
        return text.Length == 0 ? $"{status}{cut}".TrimStart('\n') : $"{text}{cut}\n{status}";
    }

    private async Task Pump(StreamReader reader, ShellOutput output)
    {
        for (string? line = await reader.ReadLineAsync(); line is not null; line = await reader.ReadLineAsync())
        {
            output.Add(line);
        }
    }

    private void Stop(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private async Task<string> ListDirectory(IStore store, DirectoryInput input, CancellationToken cancellation = default)
    {
        List<StoreEntry> entries = await store.List(Normalize(input.Path ?? string.Empty), false, cancellation).ToListAsync(cancellation);

        return entries.Count == 0 ? "(empty)" : string.Join('\n', entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.IsDirectory ? $"{entry.Path}/" : entry.Path));
    }

    private async Task<string> FindFiles(IStore store, FindInput input, CancellationToken cancellation = default)
    {
        Regex pattern = Glob(input.Pattern);
        List<string> found = [];

        await foreach (string file in Walk(store, Normalize(input.Path ?? string.Empty), cancellation))
        {
            if (pattern.IsMatch(input.Pattern.Contains('/') ? file : file[(file.LastIndexOf('/') + 1)..]))
            {
                found.Add(file);

                if (found.Count == findLimit)
                {
                    break;
                }
            }
        }

        return found.Count == 0 ? "No files found." : string.Join('\n', found.Order(StringComparer.Ordinal)) + (found.Count == findLimit ? $"\n(stopped at {findLimit} files)" : string.Empty);
    }

    private async Task<string> SearchFiles(IStore store, SearchInput input, CancellationToken cancellation = default)
    {
        Regex pattern = new(input.Pattern, input.IgnoreCase == true ? RegexOptions.IgnoreCase : RegexOptions.None, searchTimeout);
        Regex? filter = input.Glob is null ? null : Glob(input.Glob);
        List<string> matches = [];

        await foreach (string file in Walk(store, Normalize(input.Path ?? string.Empty), cancellation))
        {
            if (filter is not null && !filter.IsMatch(input.Glob!.Contains('/') ? file : file[(file.LastIndexOf('/') + 1)..]))
            {
                continue;
            }

            string text;

            try
            {
                text = await ReadLimited(store, file, cancellation);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (text.Contains('\0'))
            {
                continue;
            }

            string[] lines = text.Split('\n');

            for (int index = 0; index < lines.Length && matches.Count < matchLimit; index++)
            {
                string line = lines[index].TrimEnd('\r');

                if (pattern.IsMatch(line))
                {
                    matches.Add($"{file}:{index + 1}:{Shorten(line, 300)}");
                }
            }

            if (matches.Count == matchLimit)
            {
                break;
            }
        }

        return matches.Count == 0 ? "No matches." : string.Join('\n', matches) + (matches.Count == matchLimit ? $"\n(stopped at {matchLimit} matches)" : string.Empty);
    }

    private async Task<string> ReadFile(IStore store, ReadInput input, CancellationToken cancellation = default)
    {
        string path = Normalize(input.Path);
        string text = await ReadLimited(store, path, cancellation);

        if (text.Contains('\0'))
        {
            throw new InvalidOperationException($"'{path}' is a binary file.");
        }

        string[] lines = text.Split('\n');

        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        int start = Math.Max(1, input.Offset ?? 1);
        int count = Math.Max(1, input.Limit ?? readLines);
        string[] part = lines.Skip(start - 1).Take(count).ToArray();

        if (part.Length == 0)
        {
            return lines.Length == 0 ? "(empty file)" : $"(the file has {lines.Length} lines; offset {start} is past the end)";
        }

        string numbered = string.Join('\n', part.Select((line, index) => $"{start + index}\t{Shorten(line.TrimEnd('\r'), lineLength)}"));
        return start - 1 + part.Length < lines.Length ? $"{numbered}\n(showing lines {start} to {start - 1 + part.Length} of {lines.Length}; use offset to read more)" : numbered;
    }

    private async Task<string> WriteFile(IStore store, WriteInput input, CancellationToken cancellation = default)
    {
        string path = Normalize(input.Path);

        if (path.Length == 0)
        {
            throw new InvalidOperationException("A file path is required.");
        }

        await store.WriteText(path, input.Content, cancellation);
        return $"Wrote {path} ({input.Content.Split('\n').Length} lines).";
    }

    private async Task<string> EditFile(IStore store, EditInput input, CancellationToken cancellation = default)
    {
        string path = Normalize(input.Path);

        if (input.OldString.Length == 0)
        {
            throw new InvalidOperationException("old_string must not be empty; to create or replace a whole file use write_file.");
        }

        if (input.OldString == input.NewString)
        {
            throw new InvalidOperationException("old_string and new_string are the same.");
        }

        string text = await ReadLimited(store, path, cancellation);
        int count = 0;

        for (int at = text.IndexOf(input.OldString, StringComparison.Ordinal); at >= 0; at = text.IndexOf(input.OldString, at + input.OldString.Length, StringComparison.Ordinal))
        {
            count++;
        }

        if (count == 0)
        {
            throw new InvalidOperationException("old_string was not found in the file. It must match exactly, including whitespace.");
        }

        if (count > 1 && input.ReplaceAll != true)
        {
            throw new InvalidOperationException($"old_string matches {count} times. Include more surrounding text to make it unique, or set replace_all to replace every occurrence.");
        }

        await store.WriteText(path, text.Replace(input.OldString, input.NewString, StringComparison.Ordinal), cancellation);
        return $"Edited {path} ({count} {(count == 1 ? "replacement" : "replacements")}).";
    }

    private async Task<string> DeleteFile(IStore store, PathInput input, CancellationToken cancellation = default)
    {
        string path = Normalize(input.Path);

        if (path.Length == 0)
        {
            throw new InvalidOperationException("A mounted folder itself cannot be deleted.");
        }

        await store.Delete(path, cancellation);
        return $"Deleted {path}.";
    }

    private async Task<string> MoveFile(IStore store, MoveInput input, CancellationToken cancellation = default)
    {
        string from = Normalize(input.From);
        string to = Normalize(input.To);

        if (from.Length == 0 || to.Length == 0)
        {
            throw new InvalidOperationException("Both paths must name a file or a folder.");
        }

        await store.Move(from, to, cancellation);
        return $"Moved {from} to {to}.";
    }

    private async Task<string> ReadLimited(IStore store, string path, CancellationToken cancellation = default)
    {
        await using Stream stream = await store.Open(path, StoreOpenMode.Read, cancellation);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];

        for (int read = await stream.ReadAsync(chunk, cancellation); read > 0; read = await stream.ReadAsync(chunk, cancellation))
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > readLimit)
            {
                throw new IOException($"The file '{path}' is larger than {readLimit / 1024 / 1024} MB.");
            }
        }

        buffer.Position = 0;
        using StreamReader reader = new(buffer, Encoding.UTF8, true);
        return await reader.ReadToEndAsync(cancellation);
    }

    private async Task<string> CopyFile(IStore store, MoveInput input, CancellationToken cancellation = default)
    {
        string from = Normalize(input.From);
        string to = Normalize(input.To);

        await store.Copy(from, to, cancellation);
        return $"Copied {from} to {to}.";
    }

    private string Where(IReadOnlyList<string> mounts, bool hasWorkspace)
        => hasWorkspace
            ? $" A path is relative to the workspace, your working directory{(mounts.Count == 0 ? string.Empty : $", except that one starting with the path of a mounted folder is inside that folder (the mounted folders are: {string.Join(", ", mounts)})")}. An absolute path is a path on this machine, which only works if the workspace allows it."
            : mounts.Count == 0 ? string.Empty : $" Paths start with the path of a mounted folder. The mounted folders are: {string.Join(", ", mounts)}.";

    private async IAsyncEnumerable<string> Walk(IStore store, string directory, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        Stack<string> pending = new([directory]);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            List<StoreEntry> entries = await store.List(current, false, cancellation).ToListAsync(cancellation);

            foreach (StoreEntry entry in entries.Where(entry => !entry.IsDirectory).OrderBy(entry => entry.Path, StringComparer.Ordinal))
            {
                yield return Join(current, entry.Path);
            }

            foreach (StoreEntry entry in entries.Where(entry => entry.IsDirectory && entry.Path != ".git").OrderByDescending(entry => entry.Path, StringComparer.Ordinal))
            {
                pending.Push(Join(current, entry.Path));
            }
        }
    }

    private string Join(string directory, string name)
        => directory.Length == 0 ? name : directory.EndsWith('/') ? directory + name : $"{directory}/{name}";

    private string Normalize(string raw)
    {
        string path = raw.Replace('\\', '/').Trim();
        return Path.IsPathRooted(path) ? path : path.Resolved() ?? path;
    }

    private Regex Glob(string pattern)
    {
        StringBuilder expression = new("^");

        for (int index = 0; index < pattern.Length; index++)
        {
            char character = pattern[index];

            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                bool slash = index + 2 < pattern.Length && pattern[index + 2] == '/';
                expression.Append(slash ? "(?:.*/)?" : ".*");
                index += slash ? 2 : 1;
            }
            else if (character == '*')
            {
                expression.Append("[^/]*");
            }
            else if (character == '?')
            {
                expression.Append("[^/]");
            }
            else
            {
                expression.Append(Regex.Escape(character.ToString()));
            }
        }

        return new Regex(expression.Append('$').ToString(), RegexOptions.None, searchTimeout);
    }

    private string Shorten(string line, int length)
        => line.Length > length ? $"{line[..length]}... ({line.Length - length} more characters)" : line;

    /// <summary>
    /// The arguments of the tool that asks the user a question.
    /// </summary>
    /// <param name="Question">The question to put to the user.</param>
    internal sealed record AskInput(string Question);

    /// <summary>
    /// The arguments of a tool that takes one path.
    /// </summary>
    /// <param name="Path">The path of the file or folder.</param>
    internal sealed record PathInput(string Path);

    /// <summary>
    /// The arguments of the tool that lists a folder.
    /// </summary>
    /// <param name="Path">The folder, or <see langword="null" /> for the mounted folders.</param>
    internal sealed record DirectoryInput(string? Path = null);

    /// <summary>
    /// The arguments of the tool that finds files by name.
    /// </summary>
    /// <param name="Pattern">The glob pattern.</param>
    /// <param name="Path">The folder to search, or <see langword="null" /> for all mounted folders.</param>
    internal sealed record FindInput(string Pattern, string? Path = null);

    /// <summary>
    /// The arguments of the tool that searches file contents.
    /// </summary>
    /// <param name="Pattern">The regular expression.</param>
    /// <param name="Path">The folder to search, or <see langword="null" /> for all mounted folders.</param>
    /// <param name="Glob">A glob pattern the files must match, or <see langword="null" /> for any.</param>
    /// <param name="IgnoreCase">Whether case is ignored.</param>
    internal sealed record SearchInput(string Pattern, string? Path = null, string? Glob = null, [property: JsonPropertyName("ignore_case")] bool? IgnoreCase = null);

    /// <summary>
    /// The arguments of the tool that reads a file.
    /// </summary>
    /// <param name="Path">The path of the file.</param>
    /// <param name="Offset">The first line to return, starting at 1.</param>
    /// <param name="Limit">The number of lines to return.</param>
    internal sealed record ReadInput(string Path, int? Offset = null, int? Limit = null);

    /// <summary>
    /// The arguments of the tool that writes a file.
    /// </summary>
    /// <param name="Path">The path of the file.</param>
    /// <param name="Content">The whole new content.</param>
    internal sealed record WriteInput(string Path, string Content);

    /// <summary>
    /// The arguments of the tool that edits a file.
    /// </summary>
    /// <param name="Path">The path of the file.</param>
    /// <param name="OldString">The text to replace.</param>
    /// <param name="NewString">The text to put in its place.</param>
    /// <param name="ReplaceAll">Whether every occurrence is replaced and not only a unique one.</param>
    internal sealed record EditInput(string Path, [property: JsonPropertyName("old_string")] string OldString, [property: JsonPropertyName("new_string")] string NewString, [property: JsonPropertyName("replace_all")] bool? ReplaceAll = null);

    /// <summary>
    /// The arguments of the tools that move or copy.
    /// </summary>
    /// <param name="From">The path to move or copy.</param>
    /// <param name="To">The path it goes to.</param>
    internal sealed record MoveInput(string From, string To);

    /// <summary>
    /// The arguments of the tool that runs a shell command.
    /// </summary>
    /// <param name="Command">The command line.</param>
    /// <param name="WorkingDirectory">The folder to run in, or <see langword="null" /> for the session's scratch folder.</param>
    /// <param name="TimeoutSeconds">How long the command may run, or <see langword="null" /> for the default.</param>
    internal sealed record ShellInput(string Command, [property: JsonPropertyName("working_directory")] string? WorkingDirectory = null, [property: JsonPropertyName("timeout_seconds")] int? TimeoutSeconds = null);

    private sealed class ShellOutput(int limit)
    {
        private readonly StringBuilder text = new();

        private readonly object gate = new();

        /// <summary>
        /// Gets a value indicating whether lines were dropped because the output reached its limit.
        /// </summary>
        public bool IsCut { get; private set; }

        /// <summary>
        /// Adds a line of output, or drops it when the limit is reached.
        /// </summary>
        /// <param name="line">The line.</param>
        public void Add(string line)
        {
            lock (gate)
            {
                if (text.Length >= limit)
                {
                    IsCut = true;
                    return;
                }

                text.AppendLine(line);
            }
        }

        public override string ToString()
        {
            lock (gate)
            {
                return text.ToString();
            }
        }
    }
}
