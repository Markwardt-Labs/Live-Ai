# Subscription status

The providers' status readers (`ClaudeUsage`, `CodexLimits`) read how much of a subscription is used, for `IPromptEngine.GetWindows`, which `IPrompter.Test` reports with its result. Nothing in the library acts on the answer: it only reports it, and what to do about a nearly used-up subscription is the caller's decision.

## Reading the status

Each tool reads its own status (`IPromptEngine.GetWindows`), and the provider reaches it through its engine after a successful test, so that a caller reads the status without knowing which tool it is. A read that fails leaves the windows `null` and does not fail the test. Each read starts a short process, through the runner's general exchange (write lines, read lines, end the process when the caller stops reading), under a 30 second limit of its own that is reported as a failure, not as the caller's cancellation. Reads are independent processes, not serialized.

For Claude Code it runs `claude -p /usage --output-format json --no-session-persistence`, which prints a result whose text has a line for the current session and one for the week of all models, each with a percentage (read as a fraction from 0 to 1) and a reset time such as `Oct 26, 6:50pm (America/Chicago)`; a time without a date means its next occurrence, a date without a year the nearest one that is not long past, and the zone is looked up by name (the local zone when unknown); lines for single models are ignored. Text with no such line means a login without limits. For Codex it runs `codex app-server`, sends `initialize`, `initialized` and `account/rateLimits/read`, takes the answer with id 2 (an error answer, or limits without a window, fails the read) and ends the process.
