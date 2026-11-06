namespace Markwardt.LiveAi;

/// <summary>
/// How a command line tool does the work of a session: how to start it and read what it writes. Only <see cref="Identifier" />, <see cref="CreateStart" /> and <see cref="Parse" /> have to be implemented: the simple form then starts one process per prompt, writes the prompt to its standard input, closes it, and reads the lines it writes until it exits. Everything else has a default that does that and nothing more (no history, no branching, no status, nothing to delete, files written for the length of the prompt). <see cref="ClaudeEngine" />, <see cref="CodexEngine" /> and <see cref="OpenCodeEngine" /> are the three the library has, each with the settings it needs to be found; the <see cref="Prompter" /> makes one from the options a caller gives, and a <see cref="PromptProvider" /> makes the sessions of it.
/// </summary>
internal interface IPromptEngine
{
    /// <summary>
    /// Gets the name of the tool, such as <c>Gemini CLI</c>, which is used in messages and stored with a session to say which engine made it. A <see cref="PromptProvider" /> only loads and deletes the sessions that carry the identifier of its engine (ignoring case).
    /// </summary>
    string Identifier { get; }

    /// <summary>
    /// Gets a value indicating whether the tool can be driven through a process that holds the conversation (<see cref="CreatePersistentStart" /> and <see cref="Attach" />), which is what lets input be given to the AI while it works (<see cref="IPromptProcess.Steer" />) and lets a session keep the process between pieces of work.
    /// </summary>
    bool CanPersist => false;

    /// <summary>
    /// Gets a value indicating whether the tool keeps the instructions a conversation began with when it resumes it, so that a change of instructions has to be told to it as a note.
    /// </summary>
    bool KeepsInstructions => false;

    /// <summary>
    /// Gets a value indicating whether the schema of the answer is given with each prompt, so that a process is not tied to one.
    /// </summary>
    bool SchemaPerTurn => false;

    /// <summary>
    /// Gets a value indicating whether the tool takes images and documents as files named on its command line, which the library writes for the length of the prompt, and not inside the prompt it reads.
    /// </summary>
    bool NeedsFiles => true;

    /// <summary>
    /// Checks whether the tool still has a session of its own.
    /// </summary>
    /// <param name="aiSessionId">The tool's session identifier.</param>
    /// <returns><see langword="true" /> if the tool would still resume the session.</returns>
    bool HasSession(string aiSessionId)
        => true;

    /// <summary>
    /// Writes a JSON Schema the way the tool accepts it.
    /// </summary>
    /// <param name="schema">The schema of the structured output, whose root is an object.</param>
    /// <returns>The schema text, adjusted to what the tool requires of a schema.</returns>
    string FormatSchema(JsonElement schema)
        => schema.GetRawText();

    /// <summary>
    /// Describes the process that runs one prompt, which is written to its standard input.
    /// </summary>
    /// <param name="start">What the process is started for.</param>
    /// <returns>The process to start, without its streams redirected.</returns>
    ProcessStartInfo CreateStart(PromptEngineStart start);

    /// <summary>
    /// Describes a process that takes any number of prompts, until its input is closed, and is handed to <see cref="Attach" />.
    /// </summary>
    /// <param name="start">What the process is started for.</param>
    /// <returns>The process to start, without its streams redirected.</returns>
    /// <exception cref="NotSupportedException">The tool cannot keep a process running (<see cref="CanPersist" /> is <see langword="false" />).</exception>
    ProcessStartInfo CreatePersistentStart(PromptEngineStart start)
        => throw new NotSupportedException($"{Identifier} takes one prompt per process.");

    /// <summary>
    /// Tells whether the tool can be shown a file of a media type, as an image or a document.
    /// </summary>
    /// <param name="mediaType">The media type of the file.</param>
    /// <returns>Whether it can.</returns>
    bool Accepts(string mediaType)
        => false;

    /// <summary>
    /// Describes the process for one prompt that comes with attachments.
    /// </summary>
    /// <param name="start">What the process is started for; <see cref="PromptEngineStart.Files" /> names the attachments that were written as files when <see cref="NeedsFiles" /> is <see langword="true" />.</param>
    /// <returns>The process to start.</returns>
    ProcessStartInfo CreateAttachmentStart(PromptEngineStart start)
        => CreateStart(start);

    /// <summary>
    /// Writes a prompt as the one line a process that takes any number of prompts reads, or as what a process for one prompt with attachments reads.
    /// </summary>
    /// <param name="input">The prompt, with its notes and the attachments the tool is given as they are (those it <see cref="Accepts" />).</param>
    /// <returns>The text to write, which for a tool that reads a prompt per line is one line without its line break.</returns>
    string Frame(PromptInput input)
        => Plain(input);

    /// <summary>
    /// Writes a prompt without attachments as what a process for one prompt reads on its standard input.
    /// </summary>
    /// <param name="input">The prompt, with its notes.</param>
    /// <returns>The text to write.</returns>
    string Plain(PromptInput input)
        => input.Notes.Count == 0 ? input.Text : $"{string.Join("\n\n", input.Notes.Select(note => $"Note from the host application: {note}"))}\n\n{input.Text}";

    /// <summary>
    /// Wraps a process that was started with <see cref="CreatePersistentStart" /> into the object that takes prompts and runs the other operations on it.
    /// </summary>
    /// <param name="process">The started process, whose streams are redirected.</param>
    /// <param name="start">What the process was started for.</param>
    /// <returns>The object that talks to the process.</returns>
    /// <exception cref="NotSupportedException">The tool cannot keep a process running (<see cref="CanPersist" /> is <see langword="false" />).</exception>
    IPromptProcess Attach(Process process, PromptEngineStart start)
        => throw new NotSupportedException($"{Identifier} takes one prompt per process.");

    /// <summary>
    /// Branches the tool's session now, or says how the tool branches by itself at the first prompt of the branch.
    /// </summary>
    /// <param name="source">The session to branch from, whose <see cref="PromptEngineStart.AiSessionId" /> or <see cref="PromptEngineStart.ForkedFrom" /> is the conversation.</param>
    /// <param name="at">The message to branch after (the branch holds the conversation up to the end of the exchange the message belongs to), or <see langword="null" /> for the whole conversation. It is one that <see cref="ReadHistory" /> returned.</param>
    /// <param name="cancellation">Cancels the copy.</param>
    /// <returns>Where the branch is.</returns>
    /// <exception cref="PromptSessionException">The tool's session could not be found or branched.</exception>
    /// <exception cref="NotSupportedException">The tool cannot branch.</exception>
    Task<PromptBranch> Branch(PromptEngineStart source, PromptHistoryPoint? at, CancellationToken cancellation = default)
        => (source.AiSessionId ?? source.ForkedFrom) is null ? Task.FromResult(new PromptBranch()) : throw new NotSupportedException($"{Identifier} cannot branch a session.");

    /// <summary>
    /// Reads the conversation the tool stored for a session.
    /// </summary>
    /// <param name="aiSessionId">The tool's session identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The entries, newest first, or none when the tool stored nothing. A tool that stores a file reads it from its end, so only what is enumerated is read.</returns>
    /// <exception cref="PromptSessionException">The stored conversation cannot be read.</exception>
    IAsyncEnumerable<PromptHistoryEntry> ReadHistory(string aiSessionId, CancellationToken cancellation = default)
        => AsyncEnumerable.Empty<PromptHistoryEntry>();

    /// <summary>
    /// Deletes the conversation the tool stored for a session. Nothing happens when the tool has none.
    /// </summary>
    /// <param name="aiSessionId">The tool's session identifier.</param>
    /// <param name="cancellation">Cancels the deletion.</param>
    /// <returns>A task that completes once the conversation is gone.</returns>
    Task DeleteSession(string aiSessionId, CancellationToken cancellation = default)
        => Task.CompletedTask;

    /// <summary>
    /// Makes the conversation the tool stored for a session resumable from another working folder, for a tool that keeps a conversation with the folder it ran in. The conversation stays where it was.
    /// </summary>
    /// <param name="aiSessionId">The tool's session identifier.</param>
    /// <param name="folder">The full path of the working folder the conversation continues in.</param>
    /// <param name="cancellation">Cancels the move.</param>
    /// <returns>A task that completes once the tool would resume the conversation in the folder.</returns>
    /// <exception cref="PromptSessionException">The conversation cannot be found.</exception>
    Task MoveSession(string aiSessionId, string folder, CancellationToken cancellation = default)
        => Task.CompletedTask;

    /// <summary>
    /// Reads one line the tool wrote to its standard output.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>What the line reports, which is nothing for a line that is not of interest.</returns>
    IReadOnlyList<PromptEngineEvent> Parse(string line);

    /// <summary>
    /// Lists the models the tool can run, for the account or API key it is used with. A tool that cannot be asked, is not there, or answers in a way that cannot be read gives an empty list; a model that cannot call tools is left out when the tool tells which those are. The default is no models, which suits an engine for a tool with a single model.
    /// </summary>
    /// <param name="apiKey">The API key the models are for, or <see langword="null" /> for the configured sign in.</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models, with the settings each one offers.</returns>
    Task<IReadOnlyList<PromptModel>> GetModels(string? apiKey = null, CancellationToken cancellation = default)
        => Task.FromResult<IReadOnlyList<PromptModel>>([]);

    /// <summary>
    /// Gets a value indicating whether the tool has a web search of its own that it can be allowed to use (<see cref="PromptEngineStart.Search" />). When it has not, a session that enables search is given the library's own <c>search_web</c> tool instead. The default is <see langword="false" />.
    /// </summary>
    bool NativeSearch => false;

    /// <summary>
    /// Gets a value indicating whether the model and its settings are fixed when the process starts, so that a process cannot serve a session after they change. The default is <see langword="false" />: the model and settings travel with every prompt.
    /// </summary>
    bool StartsWithModel => false;

    /// <summary>
    /// Gets the models to choose among when <see cref="GetModels" /> lists none (the tool could not be asked, or does not list its models), or an empty list when there are none to fall back on. The default is an empty list.
    /// </summary>
    IReadOnlyList<PromptModel> FallbackModels => [];

    /// <summary>
    /// Gets the name of the model the caller asked for at each capability, or <see langword="null" /> to pick by capability. The default is <see langword="null" />.
    /// </summary>
    PromptCapabilityModels? PreferredModels => null;

    /// <summary>
    /// Gets the values the caller asked for the settings of the model chosen for a capability (the size of the context window, say).
    /// </summary>
    /// <param name="capability">The capability the model is chosen for.</param>
    /// <returns>The values by the identifier of the setting, or <see langword="null" /> for none. The default is <see langword="null" />.</returns>
    IReadOnlyDictionary<string, string>? ModelOverrides(PromptCapability capability)
        => null;

    /// <summary>
    /// Describes the smallest process that proves the tool works, whose prompt is written to its standard input and whose output is read with <see cref="Parse" />. The engine chooses the cheapest model it can tell of (here: the first one <see cref="GetModels" /> lists) at the lowest setting of each choice, in an empty folder.
    /// </summary>
    /// <param name="apiKey">The API key to run with, or <see langword="null" /> to use the configured sign in.</param>
    /// <param name="cancellation">Cancels the choice of the model.</param>
    /// <returns>The process to start, without its streams redirected.</returns>
    async Task<ProcessStartInfo> CreateTestStart(string? apiKey, CancellationToken cancellation = default)
    {
        PromptModel? model = (await GetModels(apiKey, cancellation)).FirstOrDefault();
        return CreateStart(new PromptEngineStart { Model = model?.Id, Settings = model?.Lowest() ?? [], WorkingFolder = Path.GetTempPath(), ApiKey = apiKey, Instructions = string.Empty });
    }

    /// <summary>
    /// Reads how much of each window of the subscription's limits the tool has used, without sending a prompt.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The windows the tool reports, which are the same kind for every tool; none by default, for a tool that has no subscription limits.</returns>
    /// <exception cref="PromptSessionException">The tool did not report them.</exception>
    Task<IReadOnlyList<PromptWindow>> GetWindows(CancellationToken cancellation = default)
        => Task.FromResult<IReadOnlyList<PromptWindow>>([]);
}
