# Project

Documentation for this repository's own tooling and workflow.

## Scripts

`Scripts/` holds file-based C# apps (`dotnet run Scripts/<Name>.cs`, from the root) and tasks for automated repository actions. Only what a developer runs by hand is in the folder itself; what those are built from is in `Scripts/Support/`.

- `Scripts/Publish.cs` - cuts a release (see Publishing below). A manual, human-only action.

## Publishing

Run `Scripts/Publish.cs` locally to cut a release:

1. It prompts for the version to publish (e.g. `1.2.3`) - `Core/Core.csproj` carries no `<Version>` of its own, so this is what actually gets built and published.
2. It creates the GitHub Release (and its underlying tag) for that version locally via `gh release create` - done locally because a repo ruleset blocks the default `GITHUB_TOKEN` from creating tags.
3. It dispatches `build.yml`'s `workflow_dispatch` trigger with the version as input. The workflow verifies the dispatcher has Admin permission on the repo, refuses to run from anything but `main`, packs `Core/Core.csproj`, pushes the package to GitHub Packages and nuget.org, and uploads the `.nupkg`/`.snupkg` as release assets.
4. It waits for that run to finish, rolling the release/tag back if the workflow fails, so a failed publish never leaves one behind. If it can't confirm the run happened at all, it leaves the release in place instead, rather than risk deleting one that's still running.

## Workflows

- `.github/workflows/build.yml` - builds, verifies formatting (`dotnet format --verify-no-changes`, never applies fixes), and runs tests on every push/PR to `main`; its `publish` job (see Publishing above) only runs on `workflow_dispatch`, gated on the dispatcher having Admin permission on the repo and the run being on `main`, and pushes the package to both GitHub Packages and nuget.org.
- `.github/workflows/codeql.yml` - CodeQL security analysis on push/PR to `main` and a weekly schedule.

## Tests

`Tests/src/Unit/` covers the types with mocked dependencies. `Tests/src/Integration/` uses real files and real processes, with small shell scripts standing in for `claude`, `codex` and `opencode`, and in-process HTTP servers standing in for the OpenCode server and for model servers such as Ollama (so the integration tests need a POSIX `sh`); no test calls a real AI or needs any of the tools installed.

The integration tests that write and run script files share one xUnit collection (`Processes`) so they never run at the same time: a process forked by one test can briefly hold another test's freshly written script open, and executing it then fails as "text file busy", which showed up as rare failures in a parallel run.
