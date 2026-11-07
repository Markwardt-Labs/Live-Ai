# Usage

Runnable examples of `IPrompter` and `IPromptSession` in different situations.

## A question about a folder

```csharp
IPrompter prompter = new Prompter();

IPromptSession session = await prompter.Create(new ClaudeSetup { WorkspacePath = "/path/to/project" });   // or CodexSetup

Console.WriteLine(await session.Request("Summarize what is in this folder."));
```

The setup names the tool and carries only what that tool needs. The model is chosen for the session by the library from how capable and how hard-working it should be (see below), on the subscription already configured in `claude`. The AI can reach only the workspace (the folder given as `WorkspacePath`) and the cache of the session (temporary storage, see below); without one the library makes a workspace folder for the session in its data folder, which is kept until the session is deleted or moved elsewhere.

## Reading the answer as it is written

```csharp
using IDisposable watch = session.Subscribe(new Watcher());   // an IObserver<PromptEvent>

Console.WriteLine(await session.Request("Review Program.cs and explain each step."));
```

`Request` returns when the AI has finished. What the AI writes while it works arrives in the event stream, each message when it is written, including its progress notes. To show the text while it is still being written, take the deltas from the events:

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

## Choosing capability and effort, and letting the AI change files

```csharp
IPromptSession session = await prompter.Create(new CodexSetup { WorkspacePath = "/path/to/project" });
session.Configuration.Capability = PromptCapability.Strong;
session.Configuration.Effort = PromptEffort.High;

await session.Request("Add a unit test for the parser and make it pass.");
```

The configuration a new session starts with is set once for the prompter, on its environment: `prompter.Environment = prompter.Environment with { Capability = PromptCapability.Strong, Effort = PromptEffort.High }`. `PromptEnvironment` has `Capability`, `Effort`, `IsShellAllowed`, `IsSystemAccessAllowed`, `IsWebAllowed`, `IsImageGenerationAllowed` and `Instructions`, with the same defaults as a session has without it (`Balanced`, `High`, no shell, no system access, no web, no image generation, no instructions). The values are copied into a session when it is created, so after that its configuration holds them, and a loaded session has the ones it was left with, whatever the environment says now. The tools, MCP servers and asker are not part of this: they belong to the session object and are set on each one.

The AI never asks for permission and may create, edit and delete in its workspace and the cache, so point it only at a folder it may change. A relative path the AI uses is relative to the workspace, like in a shell. Which model runs is the library's business, see "How a model is chosen".

## A session with an API key

```csharp
IPromptSession session = await prompter.Create(new ClaudeSetup { ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") });

Console.WriteLine(await session.Request("Say hello."));
```

The key is stored with the session, in plain text in its file in the data folder (readable by the current user only on Linux and macOS), so a session that is loaded later runs on the same key. To give every new Claude Code or Codex session the same key, set it once on the environment (`prompter.Environment = prompter.Environment with { ClaudeApiKey = key }`, `CodexApiKey` for Codex): a setup without a key of its own gets it, and the session stores the key it was created with.

## Resuming later

```csharp
string id = session.Id;

// ... later, possibly in another process (prompter.List() shows the identifiers that are stored)
IPromptSession resumed = await prompter.Load(id);

Console.WriteLine(await resumed.Request("What did I ask you before?"));
```

`Load` needs only the identifier: the stored link holds the setup the session was created with, so the tool is read from it, and where the tool is found comes from the environment of the prompter that loads it. The session comes back with its workspace and its whole configuration (capability, effort, the shell, system access, web and image generation switches and the instructions); the tools, MCP servers, asker and cache belong to the session object and are set again. The prompter that loads must use the same data folder as the one that created the session, and a tool configured with another configuration folder than the one that made the session cannot find it.

## Changing capability and effort mid-conversation

```csharp
session.Configuration.Capability = PromptCapability.Max;
session.Configuration.Effort = PromptEffort.High;

await session.Request("Now make the change you proposed.");
```

Nothing happens when a setting is assigned: the session takes all of them together when the next prompt starts, so a process that is kept alive is restarted at most once however many of them are changed, and a prompt that is running keeps what it started with. A value that is not allowed throws where it is assigned and changes nothing. The tool stays fixed for the life of a session.

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
// The AI reads and edits only the workspace and the cache, only through the library's file tools, with no shell (see the shell example).
await using IPromptSession project = await prompter.Create(new ClaudeSetup { WorkspacePath = "/path/to/project" });
Console.WriteLine(project.WorkspacePath);

// IsSystemAccessAllowed = true lets the AI use any path on the machine.
await using IPromptSession open = await prompter.Create(new ClaudeSetup { WorkspacePath = "/path/to/project" });
open.Configuration.IsSystemAccessAllowed = true;
```

The workspace path is the folder the tool runs in, so Claude Code, Codex and OpenCode find the instruction files of the project (`CLAUDE.md`, `AGENTS.md`) there, as they do when you start them in that folder, and the file tools take relative paths relative to it. It is stored with the session, so `Load` brings it back; `session.WorkspacePath` reads it and `Move` changes it. The AI cannot build, test or run commands; add a tool of your own for the commands you allow.

## A working directory that can reach the machine

```csharp
await using IPromptSession session = await prompter.Create(new ClaudeSetup { WorkspacePath = "/path/to/project" });
session.Configuration.IsSystemAccessAllowed = true;
session.Configuration.IsShellAllowed = true;

await session.Request("Compare Program.cs with /etc/hosts and run the tests.");
```

With `IsSystemAccessAllowed = true` (a stored setting, false by default, that can be changed at any time) the folder is only where relative paths start, and the AI may also give absolute paths of the machine or paths that climb out of the folder, so use it only when the AI may read and write anything the program can. Without it every path, absolute or not, stays inside the folder. The workspace is also where the shell starts.

## Giving the AI data and collecting what it makes

```csharp
await using IPromptSession session = await prompter.Create(new ClaudeSetup());
await session.Cache.WriteText("todo.md", "- write the report");

await session.Request("Add three items to CACHE:/todo.md and put a summary in CACHE:/summary.md.");

Console.WriteLine(await session.Cache.ReadText("summary.md"));
```

`session.Cache` is an in-memory `IStore` that belongs to the session object: it starts empty, nothing in it is stored, and it is lost when the session is disposed. The AI reaches it with paths that start with `CACHE:` (`CACHE:/folder/file.txt`), next to the workspace, and can copy and move between the two. It is where to put what the AI should read (a document from a database, a file too large for a prompt) and where to find what it makes (a summary, generated images) without touching the disk. A helper the AI starts with `delegate` shares the cache, and a branch gets an empty one of its own.

## Giving the AI tools

```csharp
public sealed record Lookup(string Name);
public sealed record Customer(string Name, string Plan, int Seats);

await using IPromptSession session = await prompter.Create(new ClaudeSetup());

session.Transient.Tools.Add(
    PromptTool.Create<Lookup, Customer>("find_customer", "Finds a customer by name and returns its plan and seats.", async (input, cancellation) => await customers.Find(input.Name, cancellation)));

Console.WriteLine(await session.Request("How many seats does Acme have?"));
```

The AI calls `find_customer` itself when it needs it, and the call runs in your process.

## Letting the AI ask the user

```csharp
await using IPromptSession session = await prompter.Create(new ClaudeSetup());
session.Transient.Asker = async (question, cancellation) =>
{
    Console.WriteLine($"The AI asks: {question}");
    return await Task.Run(Console.ReadLine, cancellation) ?? string.Empty;
};

Console.WriteLine(await session.Request("Rename the project folder to whatever I prefer."));
```

`Asker` is a `PromptAsker`, a function from the question to the reply. While it is set the AI has an `ask_user` tool, and it calls it when it needs something only you can say; the reply you return is the tool's result. Leave it null and the AI is never offered the tool, so it must decide on its own. The request waits for your reply.

## Letting the AI run commands

```csharp
await using IPromptSession session = await prompter.Create(new ClaudeSetup());
session.Configuration.IsShellAllowed = true;

Console.WriteLine(await session.Request("Run the unit tests in /path/to/project and tell me which ones fail."));
```

The `shell` tool runs any command on this machine with the rights of the user your program runs as, so enable it only for an AI and a task you trust that far; the confinement of the file tools does not limit it. It is off by default. Commands start in the workspace in the workspace, unless the AI gives a working directory, and a command that runs longer than its timeout is stopped.

## Letting the AI look at images and read the web

```csharp
session.Configuration.IsWebAllowed = true;

await session.Request("Look at docs/design.png, then read https://example.com/spec and say what differs.");
```

Every session has a `view_image` tool for a PNG, JPEG, GIF or WebP file (up to 5 MB). It reaches what the file tools reach: the cache through `CACHE:` always, and the workspace only when the session has a workspace path, inside it unless the session allows system access. A tool can only answer in text, so the image is handed to the AI as an input of its own with the image attached, steered into the turn it is working on, or sent after it when none is running. It is offered while the selected model takes images and the engine can be shown images. `IsWebAllowed`, off by default, is described below.

## The web and the tools every session has

```csharp
session.Configuration.IsWebAllowed = true;

await session.Request("Find out what changed in the latest stable .NET release and compare notes.md with notes-old.md.");
```

`IsWebAllowed` gives the AI the web with no restriction, as this machine with its network access, so addresses on the local network and on this machine are reachable too. It has two parts, with no key and no other software. A `fetch_url` tool reads text from an http or https address (HTML reduced to readable text, binary content refused, cut at 40000 characters unless the AI asks for up to 200000). For searching, when the tool has a web search of its own (Claude Code and Codex do) the AI is simply allowed to use it, and the library does not see what it searches; otherwise (OpenCode) the AI is given a `search_web` tool that reads DuckDuckGo's plain HTML page. That page is not an API, so the tool is best effort: it can change or turn the library away. The library spaces its requests at least two seconds apart, and when DuckDuckGo turns a search away it pauses searching for a minute and tells the AI so. The web is off by default, and changing the switch ends a kept process of an engine that is told about it when it starts.

Four more tools are offered to every session, with no switch: `current_time` (the date and time in UTC and the local zone, or any zone the AI names, since a model cannot know it), `tool_search` (finds a tool by words and gives its description and input schema, over the library's own tools and yours, not those of external MCP servers), `diff_files` (a unified diff of two text files of the workspace or the cache) and `delegate`. `delegate` hands a self-contained task to a helper AI of the same model with the same tools, workspace, cache and settings, and returns its answer, so that the details stay out of the AI's own conversation; with `share_context` the helper continues a fork of the conversation as it stood after the last finished request, and is told the request being worked on now (a tool's session id only becomes known when a request ends, so the fork cannot hold the request in flight), instead of knowing nothing. A helper cannot delegate in turn, at most three run at once, its usage is added to the session's, and it is removed when it is done. Their names are taken, so your own tools cannot use them.

## Making images

```csharp
prompter.Environment = prompter.Environment with { ImageGeneration = new OpenAiImageSetup { ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") } };
// or a Stable Diffusion server on this machine (AUTOMATIC1111 or Forge, started with its API on):
// prompter.Environment = prompter.Environment with { ImageGeneration = new LocalImageSetup { Server = new Uri("http://localhost:7860"), Models = new PromptCapabilityModels { Fast = "turbo.safetensors", Balanced = "base.safetensors", Strong = "base.safetensors", Max = "refined.safetensors" } } };

IPromptSession session = await prompter.Create(new ClaudeSetup { IsImageGenerationAllowed = true });   // or session.Configuration.IsImageGenerationAllowed = true
await session.Request("Make three draft icons of a lighthouse, 256x256, save them as art/icons/lighthouse, then tell me which one is best after you look at them.");
```

While the session allows it and the environment has an `ImageGeneration` setup, the AI has a `generate_image` tool. It gives a description and a path, and optionally a size, a quality (`draft`, `standard` or `high`) and a count of 1 to 4; the image is saved as a file where the AI says, with the extension of what was made, numbered when there are several, and a file that is there is never replaced. What else the AI can ask for depends on the service: OpenAI can make a transparent background (not with `gpt-image-2`, which the API refuses it for, so the tool does not offer it there); Stable Diffusion takes a negative prompt and a seed and reports the seed it used. The tool does not show the AI the image: the AI can look at it with `view_image`.

The AI chooses how capable the image model is, for each task, with the `capability` argument of `generate_image`: with OpenAI `fast` is the mini model, `balanced` the flare model and `strong` or `max` the sunburst model, the newest of that kind the key can use (a fixed model of each kind when the service cannot be asked), and with Stable Diffusion it is the checkpoint you mapped to that capability in `Models` (`Models` is required; give one checkpoint for all capabilities with `PromptCapabilityModels.All(...)` and the AI is not offered the choice). Models differ in price and in what they can do, so the AI uses a fast one for sketches and many variants and a strong one for a final image. Only the switch is stored with the session; the service and its key come from the environment of the prompter that loads it, and a change of the environment reaches the next prompt.

An image costs money or a graphics card for a while. The key is given by your code (read it from where you keep secrets), lives in the environment and is not stored with sessions, and is never logged. The setup only says how to reach the service. What to draw, the size, the quality, the count, a negative prompt and a seed are for the AI to ask for through the tool, and the user does not prompt the generator directly. For Stable Diffusion the quality becomes a number of steps the library chooses (8, 20 and 35), so a turbo model that wants fewer is not tuned for; A checkpoint is named by the title the server lists, which `LocalImageSetup.ListModels()` returns (pass the server's address for another server; it throws `InvalidOperationException` with the reason when the server cannot be asked).

## Sending images and files with a prompt

```csharp
PromptAttachment screenshot = await PromptAttachment.FromFile("/path/to/screenshot.png");
PromptAttachment notes = await PromptAttachment.FromFile("/path/to/notes.md");

Console.WriteLine(await session.Request("What is wrong in this screenshot? The notes say what it should look like.", new PromptRequestOptions { Attachments = [screenshot, notes] }));

PromptAttachment fromMount = await project.ReadAttachment("docs/diagram.png");   // from any IStore
Review review = await session.Request<Review>("Review this diagram.", new PromptRequestOptions { Attachments = [fromMount] });
```

The image is shown to the AI, the markdown file is put into the prompt as text, and a PDF would be shown to Claude Code (Codex refuses it with `NotSupportedException`, as it does any type it cannot take). Files that the AI should read in parts are better put in the cache or the workspace than attached.

## Disposing a session

```csharp
await using IPromptSession session = await prompter.Create(new ClaudeSetup());

await session.Request("Read the project and remember its layout.");
await session.Request("Which folder holds the tests?");
```

A session keeps the tool's process alive between prompts, so the second prompt reuses the process of the first, which saves a start per prompt. The capability and effort can be changed between prompts without ending it (OpenCode starts a new process for a changed model or context size). Disposing the session ends the process and stores the configuration if it changed: `await using` (or `DisposeAsync`) lets a running prompt finish first, and `Dispose` cancels it and ends the process at once.

## Watching everything the AI does

```csharp
using IDisposable watch = session.Subscribe(new Watcher());   // an IObserver<PromptEvent>

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

`Request` returns the answer text only. Everything else the AI does while it works reaches the event stream as it happens, for a request in the main conversation (the default mode; a fork's events stay in the fork). The cost is only reported by Claude Code, and `session.Usage` adds it up.

## Setting up a session before a prompt

```csharp
session.Configuration.Capability = PromptCapability.Fast;
session.Configuration.Effort = PromptEffort.Low;
session.Transient.Tools.Add(PromptTool.Create<Lookup, Customer>("find_customer", "Finds a customer.", (input, cancellation) => customers.Find(input.Name, cancellation)));

string answer = await session.Request("Summarize project/README.md in one line.");
```

The configuration and the tools belong to the session and apply from the next prompt on; a request has only its schema, its mode and whether it interrupts (and, like `Send`, files and a note).

## Reading the subscription status

```csharp
PromptTestResult result = await prompter.Test(new ClaudeSetup());   // the same shape for Codex
IReadOnlyList<PromptWindow> windows = result.Windows ?? [];

foreach (PromptWindow window in windows)
{
    // Claude Code: "Session", "Week", "Week (Opus)"; Codex: "5 hours", "Week", whatever the plan has.
    Console.WriteLine($"{window.Name}: {window.Used:P0} of {window.Length.TotalHours} hours, resets {window.ResetsAt}, pace {window.Pace:0.0}");
}

prompter.Environment = prompter.Environment with { ClaudeConfigFolder = "/accounts/second" };
PromptTestResult other = await prompter.Test(new ClaudeSetup());   // another account
```

`Pace` is how fast a window was being used when it was read: 1 uses all of it exactly when it resets, 2 would use it up halfway, and it is `null` when the reset time is not known or the window had only just started. Windows need a subscription login, so they are `null` on an API key, for OpenCode and when the tool cannot be read. The read costs nothing beyond the test prompt.

## Checking that a tool works

```csharp
PromptTestResult result = await prompter.Test(new CodexSetup { ApiKey = Environment.GetEnvironmentVariable("CODEX_API_KEY") });
Console.WriteLine(result.IsAvailable ? "Codex works." : $"Codex is not available: {result.Error}");
```

The tool is run with a tiny prompt, so a missing command, a missing login and a wrong key all show up as a reason instead of as a failure in the middle of real work. The test ends as soon as the tool has answered and has no time limit of its own, so a model that is slow to load or to think is waited for; pass a `CancellationToken` to give up sooner.

## Trying two approaches from one point

```csharp
await session.Request("Read the project and propose how to add caching.");

await using IPromptSession alternative = await session.Fork();

await session.Request("Implement it with an in-memory dictionary.");
await alternative.Request("Implement it with a file cache.");
```

The branch starts with everything the session has said and done and then goes its own way; each is resumable with its own `Id`. It has the same configuration, tools and workspace, and an empty cache of its own, so give the branch a folder of its own with `Move` if both write to it.

## Going back to an earlier point

```csharp
List<PromptHistoryEntry> history = await session.GetHistory().Take(50).ToListAsync();
PromptHistoryEntry proposal = history.First(entry => entry.Event is PromptMessageEvent && entry.Text.Contains("caching"));

await using IPromptSession branch = await session.Fork(proposal);   // a new session that holds the conversation up to that point
await session.Rewind(proposal);                                       // or take this session itself back there
```

An entry of the history is a point: the conversation is kept up to the end of the exchange it belongs to, and everything after is no longer part of the one that was branched or rewound. A rewound session keeps its `Id`; the original conversation stays in the tool's own storage.

## Moving a session to another folder

```csharp
await using IPromptSession session = await prompter.Create(new ClaudeSetup { WorkspacePath = "/path/to/project" });

await session.Move("/path/to/other-project");   // the same conversation, now working in another folder
await session.Move(null);                       // back to a folder the library makes for the session in its data folder (empty)
```

The conversation goes along: Claude Code finds a conversation by the folder it ran in, so the library copies it to the new folder, and the other tools need nothing. The session keeps its `Id` and settings, ends a kept process, and leaves the old folder alone. The folder becomes the `WorkspacePath` of the session (with the system access switch it had) and comes back with every `Load`. It fails with `DirectoryNotFoundException` for a folder that does not exist.

## Telling the AI something with an input

```csharp
await session.Send("What is left to do?", new PromptSendOptions { Notes = ["The build now passes; the failing test you saw was fixed by hand."] });
```

The note comes with the input as a message from the application, not as something the user said, and stays in the conversation. It costs no answer of its own. `Request` takes the same `Notes` (and `Attachments`) in its `PromptRequestOptions`, which derive from `PromptSendOptions`, the options `Send` takes.

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

The instructions are passed to the tool as a system prompt. They are stored with the session, so a `Load` brings them back, and they can be changed between prompts; the next prompt follows the new ones: Claude Code renders its system prompt again, and Codex is given the new instructions in a note (a developer message) with the prompt. Clearing them tells the AI that the earlier ones no longer apply.

## Using an MCP server that exists already

```csharp
session.Transient.McpServers["issues"] = PromptMcpServer.Http("https://issues.example.com/mcp", bearerTokenVariable: "ISSUES_TOKEN");
session.Transient.McpServers["git"] = PromptMcpServer.Stdio("uvx", "mcp-server-git", "--repository", "/path/to/project");

Console.WriteLine(await session.Request("List the open issues that mention the parser, and the commits that touched it."));
```

The tools of both servers are offered next to the library's own, and all of them are allowed, so add only servers you trust. The token is read from the `ISSUES_TOKEN` environment variable and is never stored.

## Giving input at any time and watching the output

```csharp
using IDisposable watch = session.Subscribe(new Watcher());   // an IObserver<PromptEvent>

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

`Interrupt()` stops the running prompt (it ends normally, with a `PromptInterruptedEvent` in the event stream), and the next request runs in the same conversation once the session is waiting again. To change course without stopping the AI, give it more input with `Send`, which it takes into the work it is doing.

## Deleting sessions

```csharp
await session.DisposeAsync();
await prompter.Delete(session.Id);   // the stored link, the tool's conversation and the folder the library made for it
```

`prompter.List()` shows what is stored, so the identifiers need not be kept.

## Reading a long conversation in pages

```csharp
List<PromptHistoryEntry> page = await session.GetHistory().Take(50).ToListAsync();
List<PromptHistoryEntry> older = await session.GetHistory().OlderThan(page[^1].Point).Take(50).ToListAsync();

// later: what has happened since the newest entry that was shown
List<PromptHistoryEntry> news = await session.GetHistory().NewerThan(page[0].Point).ToListAsync();
```

## Testing code that uses the library

```csharp
using Markwardt.LiveAi.Testing;

[Fact]
public async Task Reviews_CodeWithTheAi()
{
    FakePrompter prompter = new() { OnCreate = session => session.Reply(new Review("Looks fine.", 9, [])) };

    Review review = await new Reviewer(prompter).Review("var x = 1;");

    Assert.Equal(9, review.Score);
    Assert.Contains("var x = 1;", prompter.Sessions.Single().Prompts.Single().Text);
}
```

Pass a `TimeProvider` (`new FakePrompter { Time = clock }`) to run the clock of the sessions on a time the test advances. The fake session answers from a script and records the prompts, and the requests it gives out are the real ones, so options and structured answers behave as in production. A prompt that has no scripted answer fails and shows the prompt. `Created` and `Tests` record the setups the code under test passed.

## Logging what sessions do

```csharp
using ILoggerFactory logging = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
Prompter prompter = new(logger: logging.CreateLogger("LiveAi"));
```

`new Prompter(dataFolder, logger, time)` also takes a `TimeProvider` for the clock of its sessions (the pauses of the web search, the times of prompts). In a trimmed or native AOT application, `Request<T>` and `PromptTool.Create<,>` warn because they use reflection; keep the members of the types you pass.

The logger receives what the library does, not what the AI says: state changes, processes and tool servers that were started, input given to running work and branches (information and warning levels for the notable ones, debug for the routine). Prompts, answers and files are never logged. Without a logger nothing is logged.

## Running a model on this machine

```csharp
// ollama pull gemma4:26b
prompter.Environment = new PromptEnvironment { LocalSettings = new PromptServerSettings { Models = PromptCapabilityModels.All("gemma4:26b"), Contexts = PromptCapabilityContexts.All(16384) } };   // finds Ollama on http://localhost:11434/v1

await using IPromptSession session = await prompter.Create(new LocalSetup { WorkspacePath = "/path/to/project" });

Console.WriteLine(await session.Request("Which file holds the entry point?"));
```

`LocalSetup.ListModels()` lists the names a server offers for `Models` (the ones that can call tools; pass the server's address as an argument for another server), and it is empty when the server cannot be reached. Local sessions drive OpenCode 2.x (`opencode` on the `PATH`, or its path as `LocalCommand` of the environment) over the model server named by `Server` (Ollama on this machine by default; LM Studio, llama.cpp and vLLM speak the same protocol). The server, the models and the context sizes are set once for the environment in `Environment.LocalSettings` and can be overridden for one session in `LocalSetup.Overrides` (a part that is set replaces the environment's); the models must be set by one of them and say which model runs: `PromptCapabilityModels.All("gemma4:26b")` runs one model whatever the capability is, and `new PromptCapabilityModels { Fast = "small", Balanced = "medium", Strong = "large", Max = "huge" }` runs a different one for each capability of the session, so changing `Configuration.Capability` between prompts switches models. `Contexts` is the size of the context window of each capability (`PromptCapabilityContexts.All(16384)` for one size, or a size per capability, with none meaning the default), which OpenCode needs to know to compact in time and which the server has to run the model with (the default is what a loaded model reports, and otherwise Ollama's own default of 4096). The AI works only through the tools of the session (the file tools, `shell` when enabled, your own tools and `ask_user`), exactly as with the other tools. How well it does depends on the model: it must call tools reliably, and a small or heavily quantized model will often not; and on modest hardware a large model is slow (a 26B model that mostly runs on the processor can take minutes for a prompt that calls one tool, and `Test` waits for it). Input sent while a prompt runs is steered into it at its next step, and a conversation can be branched or rewound to any earlier point.

## How a model is chosen

```csharp
session.Configuration.Capability = PromptCapability.Strong;   // Fast, Balanced, Strong or Max
session.Configuration.Effort = PromptEffort.High;             // Low, Medium, High or Max
```

The tools differ in what they offer, so the library hides it: a capability and an effort are all a caller says. For Claude Code the capabilities are `haiku`, `sonnet`, `opus` and `fable`, for Codex the models its catalog describes by size, and for a local server the models it lists in order. When a tier does not exist the nearest one is used, the higher on a tie. The effort becomes the reasoning level of the chosen model: the one of the same name, the highest for `Max`, otherwise the levels are spread in order, and a model without one runs as it is. The choice is made when a prompt starts, from the models the tool lists at that moment (kept for a minute), with models the library knows as a fallback when the tool cannot be asked; changing either setting between prompts changes the model for the next prompt. Both are stored with the session.

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
Prompter prompter = new("/var/lib/my-app/ai")
{
    Environment = new PromptEnvironment { ClaudeCommand = "/opt/claude/bin/claude", ClaudeConfigFolder = "/var/lib/my-app/claude" }
};

IPromptSession session = await prompter.Create(new ClaudeSetup());
IPromptSession resumed = await prompter.Load(session.Id);
```

The environment is the prompter's and is not stored with the sessions, so a prompter that loads a session finds the tool where its own environment says (`CodexCommand`, `CodexConfigFolder` and `LocalCommand` are the others).
