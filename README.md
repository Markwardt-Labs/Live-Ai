# Live AI

[![NuGet](https://img.shields.io/nuget/v/Markwardt.LiveAi.svg?label=NuGet)](https://www.nuget.org/packages/Markwardt.LiveAi)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Build](https://github.com/Markwardt-Labs/Live-Ai/actions/workflows/build.yml/badge.svg)](https://github.com/Markwardt-Labs/Live-Ai/actions/workflows/build.yml)
![Coverage](.github/badges/badge_linecoverage.svg)

Live AI drives the Claude Code (`claude`) and OpenAI Codex (`codex`) command line tools from .NET, so the agent loop and sign in stay theirs, also with an API key. A session is a resumable conversation with a workspace and mounted folders, and everything the AI does arrives as one stream of events.

- **Requests:** ask directly, mixed into running work, or in a fork. Send files and images, typed input, and get typed output under a JSON schema.
- **Tools:** give the AI your own functions, MCP servers, a way to ask the user questions, and approve or refuse every call.
- **Control:** timeouts, retries, budgets and subscription limits, plus loops, interrupts, branching, rewinding and compaction.
- **Files:** the AI reaches files only through stores you choose (disk, zip, memory, overlays, read-only), and has no shell unless you enable one.
- **Extensible and testable:** add other tools as engines, and test your code with the fakes in `Markwardt.LiveAi.Testing`.

Built on .NET 10 with one dependency, `Microsoft.Extensions.Logging.Abstractions`; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Installing

```sh
dotnet add package Markwardt.LiveAi
```

The `claude` and/or `codex` command must be installed and signed in (or be given an API key) for the providers you use.

## Getting started

```csharp
IPromptProvider provider = IPromptProvider.Claude;   // or IPromptProvider.Codex

await using IPromptSession session = await provider.Create();
session.Configuration.Workspace = IStore.FromDisk("/path/to/project");

Console.WriteLine(await session.Request("Which file holds the entry point?"));

// Later, possibly in another process: the conversation comes back with it, and the settings are set again.
await using IPromptSession resumed = await provider.Load(session.Id);
resumed.Configuration.Workspace = IStore.FromDisk("/path/to/project");
Console.WriteLine(await resumed.Request("And what does it do?"));
```

## Documentation

| File | Covers |
| --- | --- |
| [Docs/Api.md](Docs/Api.md) | Shape and flow of the public API |
| [Docs/Usage.md](Docs/Usage.md) | Runnable usage examples |
| [Docs/Architecture.md](Docs/Architecture.md) | High-level design decisions |
| [Docs/Project.md](Docs/Project.md) | Repository tooling: scripts, publishing, CI |
| [Docs/Components/](Docs/Components) | One file per complex internal component |
