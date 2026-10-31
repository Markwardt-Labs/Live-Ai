# Tools

`PromptTool`, `PromptToolHost`, `PromptToolServer` and `ToolEndpoint` offer the caller's functions to the AI's tool. A session owns at most one server at a time; the providers only learn its address and secret.

## The server

The server answers MCP's Streamable HTTP in its simplest form: every request is a POST of one JSON-RPC message to `/mcp` and every answer is one JSON body, with no sessions and no event streams. It implements `initialize` (answering with the client's own protocol version, so a newer client is not refused), `ping`, `tools/list` and `tools/call`; a notification gets `202`, any other method a JSON-RPC "method not found", other HTTP verbs `405`. `tools/list` describes each tool with its schema and a `readOnlyHint` annotation. `tools/call` runs the handler and returns its text as the result; an exception or arguments that do not fit the input type come back as an error result (`isError`), not a protocol error, because that is the form the AI is shown and can act on. An unknown tool is a protocol error.

Requests are handled concurrently. Each handler gets a token that is cancelled when the server is disposed, so a long-running tool ends with its session. A request that is cancelled that way, or whose client has gone, ends quietly.

## Port and secret

`PromptToolHost` finds a free loopback port by listening briefly on port 0 and then binds the real listener there, retrying if another process takes the port in between. The secret is 32 random bytes in hex, compared in constant time. The listener is started on `127.0.0.1` rather than a wildcard so no firewall prompt or elevated rights are needed and nothing outside the machine can reach it.

## How each tool is told

Claude Code gets an MCP configuration as a JSON string (`--mcp-config`) with an HTTP server whose bearer header is written as `${LIVEAI_TOOLS_TOKEN}`, which Claude Code expands from its environment, plus `--allowedTools` for each tool as `mcp__liveai__<name>` (a tool that is offered but not allowed is refused in the `dontAsk` permission mode). Codex gets `-c` overrides for the server's URL, the name of the environment variable that holds the bearer token, the approval mode and a per-tool timeout just above the longest a shell command may run (so the tool's own limit does not cut a long command off); its read-only sandbox does not block the calls. In both, the secret travels in the environment only, so it is not in the process list. The AI knows a tool only by its name, description and input schema, so those carry everything it needs to use the tool well.

## The library's own tools

The shell tool, when `EnableShell` is on, goes through the server too, as one more library tool that is built once per session (again when the folder it starts in changes) and is part of the endpoint's identity, which makes a persistent process restart when it is switched. The file tools of the library (see the File tools component) go through this same server: the session builds them over the workspace and the mounts of its configuration (and offers none when there is neither) and serves them together with the caller's tools, under the same read-only rule. They are built once and reused, so the server and a persistent process are not restarted every prompt; they are rebuilt when the workspace or the mounts change. Their names are reserved, so a caller's tool cannot take one.

## Hooks

Every call of a tool the server offers (the shell, the file tools and the caller's tools) passes through the session's hooks, which the server is given when it is started (`IPromptToolHooks`, which the session implements by reading its configuration at that moment). The before hook decides: a refusal, or a hook that throws, is returned to the AI as an error result that says the call was not allowed, with the reason, and tells it not to try to get around that, and the tool does not run. The hooks take the same event records `Output` carries: the before hook a `PromptToolCallEvent`, the after hook that event, the `PromptToolResultEvent` (the text the AI was given and whether it was an error) and the duration measured around the tool. The id of the events is the AI tool's own call id when the request carries it (Claude Code sends it as `_meta."claudecode/toolUseId"` in `tools/call`, which is what makes a hook call match an event on `Output`), and otherwise the JSON-RPC id of the request. The after hook runs also for a call whose tool threw, and nothing it throws reaches the AI. A call that was refused is not reported. Hooks are not part of the endpoint, so setting one does not restart a server or a process. They see only these tools: an external MCP server is called by the AI's tool itself, and the only trace of such a call is the event it produces.

## Lifetime

The session starts a server when a prompt is sent with offered tools, keeps it across prompts so the address and secret are stable (a persistent process keeps using it), replaces it when the offered tools change, and ends it when none are offered or the session is disposed. Replacing the server changes the endpoint, which is also what makes a persistent process restart. With no tools, no server and no port exist.

## Tools of one prompt and external servers

The tools and the mounts of a session are served in one server that lasts across prompts; a request cannot add its own, so everything the AI can use is what the configuration holds when the prompt starts, plus the library's `respond` tool, which every session offers, and the `ask_user` tool, which is offered only while the configuration has an `Asker`. Both are built once per session and cached, because the served tools are compared by their handlers to decide whether the server (and a persistent process behind it) must restart; the `ask_user` handler reads the current `Asker` when it is called. The names of every library tool are reserved in the configuration case-insensitively, and serving refuses a set of tools that repeats a name, so nothing the caller adds can shadow `respond` or `ask_user`. External MCP servers are not served by the library at all: they are passed to the AI's tool, which starts or reaches them itself, so the library only has their definitions.
