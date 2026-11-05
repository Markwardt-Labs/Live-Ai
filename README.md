# Live AI

[![NuGet](https://img.shields.io/nuget/v/Markwardt.LiveAi.svg?label=NuGet)](https://www.nuget.org/packages/Markwardt.LiveAi)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Build](https://github.com/Markwardt-Labs/Live-Ai/actions/workflows/build.yml/badge.svg)](https://github.com/Markwardt-Labs/Live-Ai/actions/workflows/build.yml)
![Coverage](.github/badges/badge_linecoverage.svg)

Live AI drives the Claude Code (`claude`), OpenAI Codex (`codex`) and OpenCode (`opencode`, for local models) command line tools from .NET, so the agent loop and sign in stay theirs, also with an API key. A session is a resumable conversation with a workspace and mounted folders, and everything the AI does arrives as one stream of events.

- **Requests:** ask directly, mixed into running work, or in a fork. Send files and images, typed input, and get typed output under a JSON schema.
- **Tools:** give the AI your own functions, MCP servers, a way to ask the user questions, web search and page reading, images, helper delegation, the time and file diffs, and approve or refuse every call.
- **Control:** timeouts, retries, budgets and subscription limits, plus loops, interrupts, branching, rewinding and compaction.
- **Files:** the AI reaches files only through stores you choose (disk, zip, memory, overlays, read-only), and has no shell unless you enable one.
- **Extensible and testable:** run local models through OpenCode, add other tools as engines, and test your code with the fakes in `Markwardt.LiveAi.Testing`.

Built on .NET 10 with one dependency, `Microsoft.Extensions.Logging.Abstractions`; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Installing

```sh
dotnet add package Markwardt.LiveAi
```

The command of each tool you use must be installed: `claude` and `codex` signed in (or given an API key), `opencode` (2.x) with a model server such as Ollama running for local models.

## Getting started

```csharp
IPromptProvider provider = new PromptProvider(new ClaudeEngine());       // Claude Code
// IPromptProvider provider = new PromptProvider(new CodexEngine());     // Codex
// IPromptProvider provider = new PromptProvider(new OpenCodeEngine());  // models on this machine (Ollama)

IReadOnlyList<PromptModel> models = await provider.GetModels();          // each model lists the settings it offers (effort, context size)

await using IPromptSession session = await provider.Create(new PromptSessionOptions { Model = models[0], WorkspacePath = "/path/to/project" });

Console.WriteLine(await session.Request("Which file holds the entry point?"));

// Later, possibly in another process: the conversation comes back with its workspace and its model.
await using IPromptSession resumed = await provider.Load(session.Id);
Console.WriteLine(await resumed.Request("And what does it do?"));
```

A session runs the model chosen for it, with the values of that model's settings (`session.Configuration.ModelSettings["effort"] = "high"`), and both are stored with it, as is the workspace folder. Each engine takes the command and, for Claude Code and Codex, the configuration folder; `OpenCodeEngine` takes the servers on this machine whose models it offers.

## Documentation

| File | Covers |
| --- | --- |
| [Docs/Api.md](Docs/Api.md) | Shape and flow of the public API |
| [Docs/Usage.md](Docs/Usage.md) | Runnable usage examples |
| [Docs/Architecture.md](Docs/Architecture.md) | High-level design decisions |
| [Docs/Project.md](Docs/Project.md) | Repository tooling: scripts, publishing, CI |
| [Docs/Components/](Docs/Components) | One file per complex internal component |
