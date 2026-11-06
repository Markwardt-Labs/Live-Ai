# Live AI

[![NuGet](https://img.shields.io/nuget/v/Markwardt.LiveAi.svg?label=NuGet)](https://www.nuget.org/packages/Markwardt.LiveAi)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Build](https://github.com/Markwardt-Labs/Live-Ai/actions/workflows/build.yml/badge.svg)](https://github.com/Markwardt-Labs/Live-Ai/actions/workflows/build.yml)
![Coverage](.github/badges/badge_linecoverage.svg)

Live AI drives the Claude Code (`claude`), OpenAI Codex (`codex`) and OpenCode (`opencode`, for local models) command line tools from .NET, so the agent loop and sign in stay theirs, also with an API key. A session is a resumable conversation, stored with its configuration, with a workspace folder and a temporary cache, and everything the AI does arrives as one stream of events.

- **Requests:** ask directly, mixed into running work, or in a fork. Send files and images, typed input, and get typed output under a JSON schema.
- **Tools:** give the AI your own functions, MCP servers, a way to ask the user questions, web search and page reading, images (looking at them and making them with OpenAI or a local Stable Diffusion), helper delegation, the time and file diffs.
- **Control:** usage and subscription status, interrupts, branching, rewinding and compaction.
- **Files:** the AI reaches files only through the library's file tools, in the session's workspace folder (one you give it, or one the library makes and keeps for the session) and in a temporary cache, and has no shell unless you enable one.
- **Local models and tests:** run local models through OpenCode, and test your code with the fakes in `Markwardt.LiveAi.Testing`.

Built on .NET 10 with one dependency, `Microsoft.Extensions.Logging.Abstractions`; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Installing

```sh
dotnet add package Markwardt.LiveAi
```

The command of each tool you use must be installed: `claude` and `codex` signed in (or given an API key), `opencode` (2.x) with a model server such as Ollama running for local models.

## Getting started

```csharp
IPrompter prompter = new Prompter();

// ClaudeSessionOptions, CodexSessionOptions or OllamaSessionOptions (models on this machine) name the tool
IPromptSession session = await prompter.Create(new ClaudeSessionOptions { WorkspacePath = "/path/to/project" });
session.Configuration.Capability = PromptCapability.Strong;   // how capable the model should be
session.Configuration.Effort = PromptEffort.High;             // how hard it should work

Console.WriteLine(await session.Request("Which file holds the entry point?"));
string id = session.Id;
await session.DisposeAsync();

// Later, possibly in another process: the conversation comes back with its workspace and its configuration.
await using IPromptSession resumed = await prompter.Load(id);
Console.WriteLine(await resumed.Request("And what does it do?"));
```

The library picks the model and its reasoning level in the background from the capability and the effort, so a caller never lists or names models. The options carry only what their tool needs (an API key, which is stored with the session in plain text in a file only the current user can read, the workspace, where the command is found), and the identifier alone is enough to load or delete a session.

## Documentation

| File | Covers |
| --- | --- |
| [Docs/Api.md](Docs/Api.md) | Shape and flow of the public API |
| [Docs/Usage.md](Docs/Usage.md) | Runnable usage examples |
| [Docs/Architecture.md](Docs/Architecture.md) | High-level design decisions |
| [Docs/Project.md](Docs/Project.md) | Repository tooling: scripts, publishing, CI |
| [Docs/Components/](Docs/Components) | One file per complex internal component |
