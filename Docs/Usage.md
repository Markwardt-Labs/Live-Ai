# Usage

Runnable examples of `IPromptProvider` and `IPromptSession` in different situations.

## A question about a folder

```csharp
IPromptProvider provider = new PromptProvider(new ClaudeEngine());   // or new CodexEngine()

IPromptSession session = await provider.Create();
session.Configuration.Workspace = IStore.FromDisk("/path/to/project");

Console.WriteLine(await session.Request("Summarize what is in this folder."));
```

The defaults are Claude, balanced capability and medium reasoning, on the subscription already configured in `claude`. The AI can reach only the workspace and the folders mounted to the session; a session with neither can only talk.

## Reading the answer as it is written

```csharp
using IDisposable watch = session.Output.Subscribe(new Watcher());   // an IObserver<PromptEvent>

Console.WriteLine(await session.Request("Review Program.cs and explain each step."));
```

`Request` returns when the AI has finished. What the AI writes while it works arrives on `Output`, each message when it is written, including its progress notes. To show the text while it is still being written, take the deltas from the events:

```csharp
void Show(PromptEvent output)
{
    switch (output)
    {
        case PromptDeltaEvent delta:
            Console.Write(delta.Text);       // a piece of the message being written
            break;
        case PromptMessageEvent:
            Console.WriteLine();             // the whole message, which the deltas made up
            break;
    }
}
```

## Choosing the model and letting the AI change files

```csharp
IPromptSession session = await new PromptProvider(new CodexEngine()).Create();
session.Configuration.Capability = PromptCapability.Max;
session.Configuration.Reasoning = PromptReasoning.High;
session.Configuration.Workspace = IStore.FromDisk("/path/to/project");

await session.Request("Add a unit test for the parser and make it pass.");
```

The AI never asks for permission and may create, edit and delete in its workspace and the mounted folders, so give it only what it may change (or give it with `AsReadOnly()`). A relative path the AI uses is relative to the workspace, like a current directory; because the store is a sandbox (the default), a path outside the folder is refused.

## A session with an API key

```csharp
IPromptSession session = await provider.Create(new PromptSessionOptions { ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") });

Console.WriteLine(await session.Request("Say hello."));
```

The key is used by the tool for this session only and is not stored.

## Resuming later

```csharp
string id = session.Id;

// ... later, possibly in another process (provider.List() shows the identifiers that are stored)
IPromptSession resumed = await provider.Load(id, new PromptSessionOptions { ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") });

Console.WriteLine(await resumed.Request("What did I ask you before?"));
```

`Load` continues the same conversation with the provider of the same tool (a provider over another tool refuses the session with an `ArgumentException`; `session.Provider` is the provider that made or loaded it). Only the link to the conversation is stored, so the API key is given again and every setting (capability, reasoning, mounts, tools, instructions and the rest) starts at its default and is set again through `Configuration` before the next prompt. The provider that loads must use the same data folder as the one that created the session, and an engine made with another configuration folder than the one that made the session cannot find it.

## Changing the model mid-conversation

```csharp
session.Configuration.Capability = PromptCapability.Max;
session.Configuration.Reasoning = PromptReasoning.High;

await session.Request("Now make the change you proposed.");
```

Nothing happens when a setting is assigned: the session takes all of them together when the next prompt starts, so a process that is kept alive is restarted at most once however many of them are changed, and a prompt that is running keeps what it started with. A value that is not allowed throws where it is assigned and changes nothing. The provider stays fixed for the life of a session.

## Structured output and input

```csharp
public enum Strictness { Lenient, Strict }
public sealed record ReviewRequest(string Task, string Text, Strictness Strictness);
public sealed record Review(string Summary, int Score, string[] Problems);

Review review = await session.Request<Review>(new ReviewRequest("Review the text for clarity and score it from 1 to 10.", "The cat sat on the mat.", Strictness.Strict));

Console.WriteLine($"{review.Score}: {review.Summary}");
```

An object as the input writes the prompt as JSON, and that is the whole prompt, so it carries its own instruction in `Task`. The AI is held to the schema of `Review`, and the answer arrives as a `Review`. With a text prompt, `session.Request<Review>("Review this: ...")` does the same, and a string with the data written into it puts an object after an instruction. Both throw `PromptSessionOutputException` (with the raw text) if the AI does not produce a `Review`. An output type that is not an object works too (`bool allowed = await session.Request<bool>("Is this allowed? Answer true or false.");`): it is asked for in an object with one property, `value`, and unwrapped again.

## Working on files

```csharp
// The AI reads and edits only what it is given, only through the library's file tools, with no shell (see the shell example).
await using IPromptSession session = await provider.Create();
session.Configuration.Workspace = IStore.FromDisk("/path/to/project");

// The same in one step, and a loaded session has it again (sandbox: false lets the AI use any path on the machine).
await using IPromptSession project = await provider.Create(new PromptSessionOptions { WorkspacePath = "/path/to/project", Sandbox = true });

// Or the AI works on something other than a folder on disk.
session.Configuration.Mounts["documents"] = new MyDatabaseFiles(database);
```

`MyDatabaseFiles` is an `IStore`: list, open, create folder, delete and move, where open hands back a stream for the mode asked for. The library's tools build numbered reads, search, exact-match edits and copying on top of those five operations. The AI cannot build, test or run commands; add a tool of your own for the commands you allow.

## A working directory that can reach the machine

```csharp
session.Configuration.Workspace = IStore.FromDisk("/path/to/project", sandbox: false);
session.Configuration.EnableShell = true;

await session.Request("Compare Program.cs with /etc/hosts and run the tests.");
```

Assigning `Workspace` changes the running session only. It is not stored, so a loaded session does not have it again and the tool keeps running in the folder the session was created with. To have a folder as the workspace for good (also after a load), give it to `Create` as `workspacePath`, or move the session there with `AttachTo`.

With `sandbox: false` the folder is only where relative paths start, and the AI may also give absolute paths of the machine or paths that climb out of the folder, so use it only when the AI may read and write anything the program can. The default keeps every path, absolute or not, inside the folder. A store of a folder on disk is also where the shell starts.

## Working on documents in memory

```csharp
IStore notes = IStore.FromMemory();
await notes.WriteText("todo.md", "- write the report");

await using IPromptSession session = await provider.Create();
session.Configuration.Mounts["notes"] = notes;

await session.Request("Add three items to notes/todo.md and put a summary in notes/summary.md.");

Console.WriteLine(await notes.Read("summary.md"));
```

Nothing touches the disk. The store is the place to read back what the AI made, with `List`, `ReadText` (or `Open` in `Read` mode for the bytes of a file that is not text).

## Working on a zip archive

```csharp
IZipStore archive = await IStore.FromZip("/path/to/data.zip");

await using IPromptSession session = await provider.Create();
session.Configuration.Mounts["data"] = archive;

await session.Request("Add a summary of data/report.txt to data/summary.md.");

if (archive.HasChanges)
{
    await archive.Save();
}
```

The archive is read into memory, the AI works on the copy, and the file is only written when you call `Save()` (to the same path, atomically), so the AI's changes can be inspected first, or thrown away by not saving. `Save(stream)` writes the archive somewhere else. A zip is not stored with the session, so mount it again after a `Load`.

## Working on more than one folder

```csharp
await using IPromptSession session = await provider.Create();

session.Configuration.Workspace = IStore.FromDisk("/path/to/project");
session.Configuration.Mounts["reference"] = IStore.FromDisk("/path/to/reference").AsReadOnly();
session.Configuration.Mounts["notes/scratch"] = IStore.FromDisk("/tmp/scratch");

await session.Request("Follow reference/style.md to tidy src, and keep notes in notes/scratch/notes.md.");
```

A relative path is in the workspace, unless it starts with the path of a mount: `reference/style.md` is `style.md` of the reference folder and `notes/scratch/notes.md` is `notes.md` of the scratch folder (a mount can be at any relative path, and covers what the workspace has there). The AI can copy and move between them. `AsReadOnly()` lets it read `reference/` but not change it. Without a workspace only the mounts can be reached.

## Giving the AI tools

```csharp
public sealed record Lookup(string Name);
public sealed record Customer(string Name, string Plan, int Seats);

await using IPromptSession session = await provider.Create();

session.Configuration.Tools.Add(
    PromptTool.Create<Lookup, Customer>("find_customer", "Finds a customer by name and returns its plan and seats.", async (input, cancellation) => await customers.Find(input.Name, cancellation)));

Console.WriteLine(await session.Request("How many seats does Acme have?"));
```

The AI calls `find_customer` itself when it needs it, and the call runs in your process.

## Letting the AI ask the user

```csharp
await using IPromptSession session = await provider.Create();
session.Configuration.Asker = async (question, cancellation) =>
{
    Console.WriteLine($"The AI asks: {question}");
    return await Task.Run(Console.ReadLine, cancellation) ?? string.Empty;
};

Console.WriteLine(await session.Request("Rename the project folder to whatever I prefer."));
```

While `Asker` is set the AI has an `ask_user` tool, and it calls it when it needs something only you can say; the reply you return is the tool's result. Leave it null and the AI is never offered the tool, so it must decide on its own. The request waits for your reply, so a timeout in the limits counts that time too.

## Letting the AI run commands

```csharp
await using IPromptSession session = await provider.Create();
session.Configuration.EnableShell = true;

Console.WriteLine(await session.Request("Run the unit tests in /path/to/project and tell me which ones fail."));
```

The `shell` tool runs any command on this machine with the rights of the user your program runs as, so enable it only for an AI and a task you trust that far; mounts and read-only stores do not limit it. It is off by default. Commands start in the workspace when that is a folder on disk, and otherwise in an empty scratch folder, unless the AI gives a working directory, and a command that runs longer than its timeout is stopped.

## Sending images and files with a prompt

```csharp
PromptAttachment screenshot = await PromptAttachment.FromFile("/path/to/screenshot.png");
PromptAttachment notes = await PromptAttachment.FromFile("/path/to/notes.md");

Console.WriteLine(await session.Request("What is wrong in this screenshot? The notes say what it should look like.", new PromptRequestOptions { Attachments = [screenshot, notes] }));

PromptAttachment fromMount = await project.ReadAttachment("docs/diagram.png");   // from any IStore
Review review = await session.Request<Review>("Review this diagram.", new PromptRequestOptions { Attachments = [fromMount] });
```

The image is shown to the AI, the markdown file is put into the prompt as text, and a PDF would be shown to Claude Code (Codex refuses it with `NotSupportedException`, as it does any type it cannot take). Mounted files that the AI should read in parts are better mounted than attached.

## Watching a loop

```csharp
await using IPromptLoop<string> loop = session.Loop("Check inbox/ for new files and report what you did.", PromptLoopSchedule.Every(TimeSpan.FromMinutes(5)));

using IDisposable subscription = loop.Replies.Subscribe(new Watcher());   // an IObserver<string>
```

A loop can also carry files and the other options of a request (`session.Loop("Check this folder against the diagram.", PromptLoopSchedule.Every(TimeSpan.FromMinutes(5)), new PromptRequestOptions { Attachments = [diagram] })`), and its prompt and options can be replaced with `loop.Input = "new text"` (or an object) and `loop.Options = loop.Options with { ... }`; the next send uses them. `Replies` is a plain `IObservable<string>`, so it works with System.Reactive (`loop.Replies.Subscribe(Console.WriteLine)` with its extension methods) or any observer of your own. Each completed send publishes its answer; a watcher gets the latest answer first, so it does not miss the first one, and it is told when the loop stops, normally or with the exception of a failed send.

## Keeping the process alive between prompts

```csharp
await using IPromptSession session = await provider.Create();
session.Configuration.IsPersistent = true;

await session.Request("Read the project and remember its layout.");
await session.Request("Which folder holds the tests?");

session.Configuration.IsPersistent = false;
```

The first prompt starts the tool and the second reuses the same process, which saves a start per prompt, with either provider. The model and the effort can be changed between prompts without ending it. Setting `IsPersistent` to `false` returns at once and ends the process in the background. Disposing the session ends it too: `await using` (or `DisposeAsync`) lets a running prompt finish first, and `Dispose` cancels it and ends the process at once.

## Sending a prompt repeatedly

```csharp
await using IPromptSession session = await provider.Create();
session.Configuration.Mounts["inbox"] = IStore.FromDisk("/path/to/inbox");

await using IPromptLoop<string> loop = session.Loop("Process any new files in inbox/ and move them to inbox/done/.", PromptLoopSchedule.Every(TimeSpan.FromMinutes(5)));

loop.Trigger();
loop.Input = "Process new files in inbox/ and write a one line summary of each to inbox/log.md.";
```

The first send starts when the loop is made, and the next one when the last is done and five minutes have passed since it started (`Loop(input)` without a schedule sends again as soon as each send is done, `Loop(input, PromptLoopSchedule.Manual)` only when triggered, and `loop.Schedule` can be replaced later). `Trigger()` sends at once (it does nothing while a send is running), and the new prompt is used from the next send. Disposing the loop stops it (`await using` lets a send in progress finish, `Dispose` cancels it); if a send fails, the loop stops and `loop.Task` is faulted with the exception.

## Watching everything the AI does

```csharp
using IDisposable watch = session.Output.Subscribe(new Watcher());   // an IObserver<PromptEvent>

// In the watcher:
public void OnNext(PromptEvent output)
{
    switch (output)
    {
        case PromptMessageEvent message:
            Console.WriteLine(message.Text);
            break;
        case PromptToolCallEvent call:
            Console.WriteLine($"-> {call.Name} {call.Input}");
            break;
        case PromptToolResultEvent result:
            Console.WriteLine($"<- {(result.IsError ? "failed" : "ok")}");
            break;
        case PromptUsageEvent usage:
            Console.WriteLine($"{usage.Usage.OutputTokens} tokens written, cost {usage.Usage.CostUsd}");
            break;
    }
}

await session.Request("Fix the failing test in project/.");
```

`Request` returns the answer text only. Everything else the AI does while it works reaches `Output` as it happens, for a request in the main conversation (the default mode; a fork's events stay in the fork). The cost is only reported by Claude Code, and `session.Usage` adds it up.

## Choosing the model and the limits for a session

```csharp
session.Configuration.Capability = PromptCapability.Fast;
session.Configuration.Reasoning = PromptReasoning.Low;
session.Configuration.Tools.Add(PromptTool.Create<Lookup, Customer>("find_customer", "Finds a customer.", (input, cancellation) => customers.Find(input.Name, cancellation)));
session.Configuration.Mounts["scratch"] = IStore.FromMemory();

string answer = await session.Request("Summarize project/README.md in one line.");
```

The model, the reasoning, the tools and the stores belong to the session and apply from the next prompt on; a request has only its schema, its mode and whether it interrupts (and, like `Send`, files and a note).

## Timeouts and subscription limits

```csharp
session.Configuration.Limits = new PromptSessionLimits
{
    Timeout = TimeSpan.FromMinutes(10),
    WindowLimit = new PromptWindowLimit
    {
        Reached = windows => windows.Any(window => window.Used > 0.95 || window.Pace > 1.5),
        Interval = TimeSpan.FromMinutes(2)
    }
};

try
{
    await session.Request("Refactor project/ completely.");
}
catch (PromptLimitException exception) when (exception.Limit == PromptLimit.Window)
{
    Console.WriteLine("Stopped to keep the subscription from running out.");
}
```

The prompt is cancelled when it runs longer than ten minutes or when the status, read when it starts and every two minutes, passes the limit.

## Reading the subscription status

```csharp
IReadOnlyList<PromptWindow> windows = await new PromptProvider(new ClaudeEngine()).GetWindows();   // the same shape for Codex, or session.Provider.GetWindows()

foreach (PromptWindow window in windows)
{
    // Claude Code: "Session", "Week", "Week (Opus)"; Codex: "5 hours", "Week", whatever the plan has.
    Console.WriteLine($"{window.Name}: {window.Used:P0} of {window.Length.TotalHours} hours, resets {window.ResetsAt}, pace {window.Pace:0.0}");
}

IReadOnlyList<PromptWindow> other = await new ClaudeEngine(configFolder: "/accounts/second").GetWindows();   // another account
```

`Pace` is how fast a window was being used when it was read: 1 uses all of it exactly when it resets, 2 would use it up halfway, and it is `null` when the reset time is not known or the window had only just started. It needs a subscription login, and sends no prompt, so a read takes a second or two and costs nothing.

## Checking that a tool works

```csharp
PromptTestResult result = await new PromptProvider(new CodexEngine()).Test(Environment.GetEnvironmentVariable("CODEX_API_KEY"));
Console.WriteLine(result.IsAvailable ? "Codex works." : $"Codex is not available: {result.Error}");
```

The tool is run with a tiny prompt, so a missing command, a missing login and a wrong key all show up as a reason instead of as a failure in the middle of real work. The test ends as soon as the tool has answered and has no time limit of its own, so a model that is slow to load or to think is waited for; pass a `CancellationToken` to give up sooner.

## A loop that stops by itself

```csharp
public sealed record Findings(int Open, string Summary);

using IPromptLoop<Findings> loop = session.Loop<Findings>(
    "Count the open TODO comments in project/ and summarize them.",
    PromptLoopSchedule.Every(TimeSpan.FromMinutes(10)),
    limits: new PromptLoopLimits<Findings> { Timeout = TimeSpan.FromHours(8), MaxRuns = 50, Until = findings => findings.Open == 0 });

loop.Replies.Subscribe(new Watcher());   // an IObserver<Findings>
await loop.Task;
Console.WriteLine(loop.StopReason);      // Until, MaxRuns, Timeout, WindowLimit or Disposed
```

Every reply is a `Findings`. The loop ends normally at the first limit it reaches, after publishing the reply that reached it, and `StopReason` says which. Add `WindowLimit = status => ...` to stop it before it uses up a subscription, and `WaitForWindow = windows => windows.MaxBy(window => window.ResetsAt)` (or whichever window your limit is about) to make it wait for that window to reset and carry on instead (`loop.ResumesAt` says until when).

## Trying two approaches from one point

```csharp
await session.Request("Read the project and propose how to add caching.");

await using IPromptSession alternative = await session.Fork();

await session.Request("Implement it with an in-memory dictionary.");
await alternative.Request("Implement it with a file cache.");
```

The branch starts with everything the session has said and done and then goes its own way; each is resumable with its own `Id`. It has the same configuration, tools and mounts, so mount a copy of the folder for the branch if both write to it.

## Going back to an earlier point

```csharp
List<PromptHistoryEntry> history = await session.GetHistory().Take(50).ToListAsync();
PromptHistoryEntry proposal = history.First(entry => entry.Event is PromptMessageEvent && entry.Text.Contains("caching"));

await using IPromptSession branch = await session.Fork(proposal);   // a new session that holds the conversation up to that point
await session.Rewind(proposal);                                       // or take this session itself back there
```

An entry of the history is a point: the conversation is kept up to the end of the exchange it belongs to, and everything after is no longer part of the one that was branched or rewound. A rewound session keeps its `Id`; the original conversation stays in the tool's own storage.

## Moving a session to another conversation or folder

```csharp
await using IPromptSession session = await provider.Create(new PromptSessionOptions { WorkspacePath = "/path/to/project" });

await session.AttachTo("0b1c...", new PromptSessionOptions { WorkspacePath = "/path/to/project" });     // continue a conversation the tool already has (its session or thread id)
await session.AttachTo(null, new PromptSessionOptions { WorkspacePath = "/path/to/other-project" });    // a new conversation in another folder
await session.AttachTo();                                  // a new conversation, no workspace, an empty folder of the session
```

Both arguments are the new values: a `null` session id starts a new conversation with the next prompt and a `null` path means no workspace. The session keeps its `Id` and settings, ends a kept process, and leaves the old conversation and folder alone. The `Workspace` of the configuration is set to the folder (`sandbox` as for `Create`), and is set again by every `Load`. It fails with `PromptSessionNotFoundException` when the tool has no such session and `DirectoryNotFoundException` for a folder that does not exist. Claude Code finds a conversation by the folder it ran in, so give the path the conversation was made in.

## Telling the AI something with an input

```csharp
await session.Send("What is left to do?", new PromptOptions { Notes = ["The build now passes; the failing test you saw was fixed by hand."] });
```

The note comes with the input as a message from the application, not as something the user said, and stays in the conversation. It costs no answer of its own. `Request` takes the same `Notes` (and `Attachments`) in its `PromptRequestOptions`, which derive from `PromptOptions`, the options `Send` takes.

## Keeping the context from overflowing

```csharp
PromptContext context = await session.GetContext();
Console.WriteLine($"{context.Tokens} of {context.Limit} tokens ({context.Used:P0})");

if (context.Used > 0.75)
{
    await session.Compact();
}
```

`GetContext` counts what the conversation takes of the model's window, the tool's own instructions included, and `Compact` makes the tool summarize it. Both wait for the prompt that is running.

## Showing the conversation so far

```csharp
await foreach (PromptHistoryEntry entry in session.GetHistory().Take(20))
{
    Console.WriteLine($"{entry.Event.GetType().Name}: {entry.Text}");
}
```

The history is what the tool stored, newest first and read lazily (so `Take(20)` reads only the end of the file), with the AI's tool calls and their results. It works on a session loaded in another process too.

## Custom instructions

```csharp
session.Configuration.Instructions = "Answer in French. Never edit files outside project/src.";
```

The instructions are passed to the tool as a system prompt. They can be changed between prompts, also after a `Load` (they are set again then like every other setting), and the next prompt follows the new ones: Claude Code renders its system prompt again, and Codex is given the new instructions in a note (a developer message) with the prompt. Clearing them tells the AI that the earlier ones no longer apply.

## Using an MCP server that exists already

```csharp
session.Configuration.McpServers["issues"] = PromptMcpServer.Http("https://issues.example.com/mcp", bearerTokenVariable: "ISSUES_TOKEN");
session.Configuration.McpServers["git"] = PromptMcpServer.Stdio("uvx", "mcp-server-git", "--repository", "/path/to/project");

Console.WriteLine(await session.Request("List the open issues that mention the parser, and the commits that touched it."));
```

The tools of both servers are offered next to the library's own, and all of them are allowed, so add only servers you trust. The token is read from the `ISSUES_TOKEN` environment variable and is never stored.

## Approving and watching the tools

```csharp
session.Configuration.EnableShell = true;
session.Configuration.BeforeToolCall = (call, cancellation) => Task.FromResult(
    call.Name != "shell" || call.Arguments.GetProperty("command").GetString()!.StartsWith("dotnet test")
        ? PromptToolDecision.Allow
        : PromptToolDecision.Deny("only dotnet test may run"));
session.Configuration.AfterToolCall = (call, result, duration, cancellation) =>
{
    log.Information("{Tool} took {Time} ({Failed})", call.Name, duration, result.IsError);
    return Task.CompletedTask;
};
```

The functions see every call of the shell, the file tools and your own tools before and after it runs; one that is refused is not run and the AI is told why, so it can say so or choose another way. They do not see the tools of an MCP server that exists already, which the AI's tool calls itself.

## Retrying, budgets and defaults

```csharp
session.Configuration.Limits = new PromptSessionLimits
{
    Retry = new PromptRetry { MaxAttempts = 4, InitialDelay = TimeSpan.FromSeconds(5) },
    MaxToolCalls = 20,
    Budget = new PromptBudget { MaxTokens = 500_000, MaxCostUsd = 5m },
    Timeout = TimeSpan.FromMinutes(10)
};

Console.WriteLine($"Used {session.Usage.InputTokens + session.Usage.OutputTokens} tokens so far.");
```

A failure before anything was reported (a rate limit, an overloaded service) is tried again after a wait that grows, and a prompt that makes more than twenty tool calls is stopped. The budget makes the next prompt fail with `PromptLimitException` once the session has used that much, and the timeout and the retries apply to every request made afterwards.

## Giving input at any time and watching the output

```csharp
using IDisposable watch = session.Output.Subscribe(new Watcher());   // an IObserver<PromptEvent>

await session.Send("Look at project/src and tell me what is slow.");
await session.Send("Mind that Parser.cs is generated.");   // while it works: given to the AI at once and taken into the work

// In the watcher:
//   PromptStateEvent { State: PromptSessionState.Working }   the AI started
//   PromptMessageEvent / PromptToolCallEvent / ...           what it says and does
//   PromptStateEvent { State: PromptSessionState.Waiting }   it is idle again
```

`Send` returns once the input was written to the running AI or queued for it (not when the AI has acted on it) and throws a `PromptSessionStateException` only while the session is `Responding` to a direct request. Work that fails shows up as a `PromptFailureEvent` and the session goes on.

## Asking while the AI is busy

```csharp
Review direct = await session.Request<Review>("Review project/src/Parser.cs.");   // needs State == Waiting

Review mixed = await session.Request<Review>("How far along are you, in a Review?", new PromptRequestOptions { Mode = PromptRequestMode.Mixed });
Review side = await session.Request<Review>("Review project/src/Lexer.cs.", new PromptRequestOptions { Mode = PromptRequestMode.Fork });
```

A direct request asks in the main conversation and throws if the AI is not idle. A mixed request is given to the AI with the work it is doing and answered through a `respond` tool, so it can ask about work in progress. A fork request is asked in a throwaway copy of the conversation, so it never disturbs the main one (but it cannot see what the AI is in the middle of).

## Correcting the AI while it works

```csharp
Task<string> task = session.Request("Refactor project/ to use the new logger.");
await Task.Delay(TimeSpan.FromSeconds(20));

await session.Interrupt();
string partial = await task;   // what the AI had written when it was interrupted
string reply = await session.Request("Stop. Only change project/src/Parser.cs.");
```

`Interrupt()` stops the running prompt (it ends normally, with a `PromptInterruptedEvent` on `Output`), and the next request runs in the same conversation once the session is waiting again. To change course without stopping the AI, give it more input with `Send`, which it takes into the work it is doing.

## Deleting sessions

```csharp
await session.DisposeAsync();
await provider.Delete(session.Id);   // the stored link, the tool's conversation and the scratch folder
```

The library keeps no list of sessions, so keep the identifiers you want to show.

## Reading a long conversation in pages

```csharp
List<PromptHistoryEntry> page = await session.GetHistory().Take(50).ToListAsync();
List<PromptHistoryEntry> older = await session.GetHistory().OlderThan(page[^1].Point).Take(50).ToListAsync();

// later: what has happened since the newest entry that was shown
List<PromptHistoryEntry> news = await session.GetHistory().NewerThan(page[0].Point).ToListAsync();
```

## Letting the AI edit, then deciding

```csharp
IOverlayStore overlay = IStore.FromDisk("/path/to/project").AsOverlay();
session.Configuration.Workspace = overlay;

await session.Request("Rename Parser to ExpressionParser everywhere.");

foreach (StoreChange change in await overlay.GetChanges())
{
    Console.WriteLine($"{change.Kind} {change.Path}");
}

await overlay.Apply();     // or await overlay.Discard() to throw the work away
```

The folder on disk is not touched until `Apply()`.

## Keeping a log of what the AI touches

```csharp
session.Configuration.Workspace = IStore.FromDisk("/path/to/project").Audited(entry =>
    log.Information("{Operation} {Path} {Error}", entry.Operation, entry.Path, entry.Error));
```

## Adding another command line tool

```csharp
public sealed class MyToolEngine : IPromptEngine
{
    public string Identifier => "My Tool";

    public ProcessStartInfo CreateStart(PromptEngineStart start)
    {
        ProcessStartInfo info = new("my-tool") { WorkingDirectory = start.WorkingFolder };
        info.ArgumentList.Add("--json");

        if (start.AiSessionId is { } id)
        {
            info.ArgumentList.Add("--resume");
            info.ArgumentList.Add(id);
        }

        return info;   // the prompt is written to its standard input
    }

    public IReadOnlyList<PromptEngineEvent> Parse(string line)
        => line.StartsWith("session ") ? [new PromptEngineEvent { Kind = PromptEngineEventKind.Session, Text = line[8..] }]
            : line.StartsWith("text ") ? [new PromptEngineEvent { Kind = PromptEngineEventKind.Reply, Text = line[5..] }]
            : line == "done" ? [new PromptEngineEvent { Kind = PromptEngineEventKind.Finished }]
            : [];
}

IPromptProvider tool = new PromptProvider(new MyToolEngine());
IPromptSession session = await tool.Create();

// Later: the stored session names "My Tool", so a provider over an engine of that name loads it.
IPromptSession resumed = await tool.Load(session.Id);
```

## Testing code that uses the library

```csharp
using Markwardt.LiveAi.Testing;

[Fact]
public async Task Reviews_CodeWithTheAi()
{
    FakePromptProvider provider = new() { OnCreate = session => session.Reply(new Review("Looks fine.", 9, [])) };

    Review review = await new Reviewer(provider).Review("var x = 1;");

    Assert.Equal(9, review.Score);
    Assert.Contains("var x = 1;", provider.Sessions.Single().Prompts.Single().Text);
}
```

Pass a `TimeProvider` (`new FakePromptProvider { Time = clock }`) to run timeouts, retry delays and loop schedules on a clock the test advances. The fake session answers from a script and records the prompts, and the requests it gives out are the real ones, so timeouts, retries, loops and structured answers behave as in production. A prompt that has no scripted answer fails and shows the prompt.

## Logging what sessions do

```csharp
using ILoggerFactory logging = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
PromptProvider provider = new(new ClaudeEngine(), logger: logging.CreateLogger("LiveAi"));
```

`new PromptProvider(engine, dataFolder, logger, time)` also takes a `TimeProvider` for the request timeouts, retry delays and loop schedules of its sessions. In a trimmed or native AOT application, `Request<T>`, `Loop<T>` and `PromptTool.Create<,>` warn because they use reflection; keep the members of the types you pass.

The logger receives what the library does, not what the AI says: state changes, processes and tool servers that were started, input given to running work, branches, retries and limits that stopped a request (information and warning levels for the notable ones, debug for the routine). Prompts, answers and files are never logged. Without a logger nothing is logged.

## Running a model on this machine

```csharp
// ollama pull gemma4:26b
IPromptProvider provider = new PromptProvider(new OpenCodeEngine(models: OpenCodeModels.Ollama("gemma4:26b")));

await using IPromptSession session = await provider.Create(new PromptSessionOptions { WorkspacePath = "/path/to/project" });
session.Configuration.Mounts["docs"] = IStore.FromDisk("/path/to/docs");

Console.WriteLine(await session.Request("Which file holds the entry point?"));
```

`OpenCodeEngine` drives OpenCode 2.x (`opencode` on the `PATH`, or its path as the first argument) and takes an `OpenCodeModels`, which says which model each capability uses and which model providers serve them. The simple cases are factories: `OpenCodeModels.Configured("anthropic/claude-sonnet")` is one model of OpenCode's own configuration, `OpenCodeModels.Ollama("gemma4:26b")` one local Ollama model, and `OpenCodeModels.ByCapability([(PromptCapability.Fast, "gemma4:e2b"), (PromptCapability.Max, "gemma4:26b")])` a model for each capability (a name with a `/` is a model of OpenCode's own configuration instead of a local one, and a capability that is not given is left to OpenCode's configuration). The address of the Ollama server and the size of its context window are optional arguments of the factories; the context size is left to OpenCode unless you give it, so give it when the server runs the model with a different size (`OLLAMA_CONTEXT_LENGTH`): `OpenCodeModels.Ollama("gemma4:26b", contextLimit: 16384)`. The advanced case is the record itself: `new OpenCodeModels { Default = "lmstudio/qwen3-coder", Providers = [new OpenCodeModelProvider { Id = "lmstudio", BaseUrl = "http://localhost:1234/v1", Models = [new OpenCodeModel { Id = "qwen3-coder" }] }] }` serves a model from any OpenAI compatible server (llama.cpp, LM Studio, vLLM). `Fast`, `Balanced` and `Max` give those capabilities a model of their own, and a capability without one uses `Default`. The AI works only through the tools of the session (the file tools, `shell` when enabled, your own tools and `ask_user`), exactly as with the other tools. How well it does depends on the model: it must call tools reliably, and a small or heavily quantized model will often not; and on modest hardware a large model is slow (a 26B model that mostly runs on the processor can take minutes for a prompt that calls one tool). Input sent while a prompt runs is steered into it at its next step, and a conversation can be branched or rewound to any earlier point.

## Choosing the models from what is available

```csharp
IReadOnlyList<PromptModel> installed = await new LocalModelCatalog().List();   // the models of Ollama that can call tools
IReadOnlyList<PromptModel> codex = await new CodexModelCatalog().List();       // what the signed in account offers
IReadOnlyList<PromptModel> claude = await new ClaudeModelCatalog().List();     // the aliases haiku, sonnet and opus

IPromptProvider provider = new PromptProvider(new CodexEngine(models: new PromptModels { Fast = "gpt-5.6-luna", Max = codex.Last().Id }));
IPromptProvider local = new PromptProvider(new OpenCodeEngine(models: OpenCodeModels.ByCapability([(PromptCapability.Fast, installed[0].Id), (PromptCapability.Max, installed[^1].Id)])));
```

A catalog needs no engine, only the arguments the tool's engine takes (the command and configuration folder for Codex, the address of the server for local models), so the models can be listed before the provider that uses them is built. Listing never throws because the tool is missing: a tool that is not installed, a server that is not running and an answer that cannot be read all give an empty list. Each `PromptModel` has the name to assign, and a context size and the reasoning efforts it accepts where the tool tells them. `PromptModels` assigns a model to each capability for `ClaudeEngine` and `CodexEngine` (`PromptModels.Only(name)` and `PromptModels.ByCapability([...])` are the shortcuts); a capability without a model of its own uses `Default`, and when that is not set either, the tool's own model (`haiku`, `sonnet` and `opus` for Claude Code, `gpt-5.6-luna`, `gpt-5.6-terra` and `gpt-5.6-sol` for Codex). Codex lowers a reasoning level that a model you chose does not accept to the highest one it does. Names are not checked when the provider is built; a model that does not exist fails at the first prompt, with the tool's own message.

## Handling failures and cancelling

```csharp
using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));

try
{
    Console.WriteLine(await session.Request("Run the whole test suite.", null, cancellation: timeout.Token));
}
catch (PromptSessionException exception)
{
    Console.WriteLine($"The AI could not finish: {exception.Message}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopped.");
}
```

Cancelling ends the tool's process and everything it started.

## Tools that are not on the PATH

```csharp
IPromptProvider claude = new PromptProvider(
    new ClaudeEngine("/opt/claude/bin/claude", configFolder: "/var/lib/my-app/claude"),
    dataFolder: "/var/lib/my-app/ai");

IPromptSession session = await claude.Create();
IPromptSession resumed = await claude.Load(session.Id);
```
