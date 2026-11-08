# Storage

`StoragePool`, `TieredStore`, `SpoolStream`, `StoredFile`, `WorkspaceTemp` and `WorkspaceTempStore` give a session three places for data besides the rest of its workspace. They are named by `PromptStorageNames` and reached by the AI and by `IPromptSession.Workspace` with the same paths.

| Place | Name | Where it lives | Lasts |
| --- | --- | --- | --- |
| Workspace | relative paths | disk, the folder of the session | until deleted |
| Session storage | `.liveai-session` | memory, spilling to files | the session object |
| Cache | `.liveai-cache` | memory, spilling to files | the session object, and a file goes after it was unused for the retention |
| Temp folder | `.liveai-temp` | a real folder in the workspace | the same terms as the cache, and removed when the last session using it ends |

The session storage and the cache are mounted into the one path space the file tools see (see the file tools), so copying from one place to another is an operation on paths. The temp folder is not mounted: it is a plain folder of the workspace, which is what lets commands of the shell reach it. The first two are not reachable from commands, because they are not on disk as files the AI can name, so the AI is told to copy a file into the workspace before a command uses it.

## Memory first, files behind it

Both mounted places are `TieredStore`s of one `StoragePool` per session. A file lives in memory as an immutable byte array or on disk as a file in the session's folder (`<data folder>/<session id>/session` or `.../cache`, named by a new GUID). Nobody can tell which: a reader gets a snapshot (memory) or a stream (disk), and a write goes through a `SpoolStream`, which starts in memory and switches to a file when the file passes the memory limit, so one large write never sits in memory at all.

`PromptStorageSettings.MemoryLimit` is one budget for both places together. When memory in use passes it, a background worker moves the least recently used file of either place to disk, one at a time, until it is under the limit again. A file that is read at least twice from disk, has been there for ten seconds and fits within three quarters of the budget moves back. The residency and the three quarters are what keep a file near the limit from moving back and forth. The move is an optimization with no effect on content: if the file changed meanwhile, the copy that was made is dropped. If the disk cannot be used (no folder, not writable, held by another session) files simply stay in memory, a failure to write backs the worker off for a minute, and the reason is logged once. The memory limit is therefore a target and not a ceiling.

There is no total limit on either place, only `FileLimit` for one file, which a write over it refuses with an error that says to keep large data in the workspace and which leaves nothing stored (the placeholder the write installed is removed). Zero for `MemoryLimit` keeps everything on disk; the maximum value never uses files.

One lock (the pool's gate) guards every store and every file of a session, so swapping a file between memory and disk is atomic for every reader, and a move between the two places is a rename of an entry. It is held only for bookkeeping, never for I/O. The time of the session's clock (`TimeProvider`) decides every age, so tests control expiry.

## The disk folder

The folder is made on first need and holds a lock file for as long as the pool lives, opened exclusively. Whatever a previous run left in the folder (the lock is free then) is deleted when the lock is taken, and a pool that ends deletes its folder. Sessions that ended without cleaning up (a crash) are found by the first session of a process that touches the data folder: it deletes the storage of every session folder whose lock is free, and nothing else, so a folder that is not named like a session, or anything in a session's folder that is not storage, is left alone. Deleting a session deletes its storage too. The folder is readable by its owner only.

## Expiry

The cache expires, the session storage never does. A file's age counts from its last use: opening it for reading or writing, being the source or target of a copy, or a move. Listing or asking for a size does not count. A timer on the session's clock (every quarter of the retention, between a second and a minute) removes the files that were not used for the retention, the disk file of each with them, and the folders that are left empty. The timer starts when something first needs it, so a session that never writes to the cache has none.

## The temp folder

`.liveai-temp` exists for data that commands produce and read. It is made lazily, by the first library code that needs it: a file tool opening a path in it, the shell starting a command, or a download. Nothing is made in a workspace that never uses it.

A folder of that name that the library did not make is never touched or mentioned: ownership is a `.gitignore` marker file in the folder (which also keeps the data out of version control), and a non-empty folder without it is left as it is and not offered to the AI. A new empty folder is taken over. The marker and the lock files are left out of the listings the AI and the caller get, so the folder looks like what is in it.

Each live session holds a lock file `.live-<session id>` in the folder. When a session first uses the folder it deletes everything in it if no other lock is held, which cleans what a crashed session left; otherwise it leaves it, since another session uses it. When a session ends it deletes its lock and, if no other lock is held, the folder. A lock file whose lock is free is stale and deleted by whoever finds it.

Expiry is judged on the session's clock, from what the session observed: the file tools tell the folder which top level entry they use (`WorkspaceTempStore`), and the same timer looks at every top level entry's signature (count, size, latest write and file access time, to a bounded depth) and counts a change as a use. What a command reads cannot be seen, so while a command runs nothing is removed, and when it ends everything in the folder counts as used at that moment. A helper that the AI starts shares the folder of the session that started it and never sweeps on its own.

## What the AI is told

The guidance gives the three places, what each is for, the file limit and the retention, that commands reach only the workspace and the temp folder, and the `download_url` tool. These numbers are part of the identity of the tool endpoint, so a change restarts a process that was kept and kept the older text.
