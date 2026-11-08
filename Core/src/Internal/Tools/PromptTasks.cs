namespace Markwardt.LiveAi;

/// <summary>
/// The background tasks of one session: shell commands and helper AIs that run while the AI that started them goes on, which it checks on, waits for and stops with the task tools.
/// </summary>
internal interface IPromptTasks : IAsyncDisposable
{
    /// <summary>
    /// Starts a shell command that keeps running after this call returns.
    /// </summary>
    /// <param name="workingFolder">The folder to run in.</param>
    /// <param name="command">The command line.</param>
    /// <param name="seconds">How long the command may run before it is stopped.</param>
    /// <returns>The text the AI is told: the identifier of the task and how to follow it.</returns>
    /// <exception cref="InvalidOperationException">The command could not be started or too many tasks run.</exception>
    string StartCommand(string workingFolder, string command, int seconds);

    /// <summary>
    /// Starts a helper that keeps working after this call returns.
    /// </summary>
    /// <param name="description">What the helper was asked, for the lists the AI reads.</param>
    /// <param name="run">The work; it is cancelled when the task is stopped or the session ends.</param>
    /// <returns>The text the AI is told: the identifier of the task and how to follow it.</returns>
    /// <exception cref="InvalidOperationException">Too many tasks run.</exception>
    string StartHelper(string description, Func<CancellationToken, Task<string>> run);

    /// <summary>
    /// Waits until a task that was not reported yet has finished, or the time is up, and tells the state of the tasks.
    /// </summary>
    /// <param name="ids">The tasks to wait for, or <see langword="null" /> for all of them.</param>
    /// <param name="timeoutSeconds">How long to wait at most; zero only reports.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The report: each task that finished since the last report with its result, and each task that runs with how long and its latest output.</returns>
    /// <exception cref="InvalidOperationException">An identifier is unknown.</exception>
    Task<string> Wait(IReadOnlyList<string>? ids, int timeoutSeconds, CancellationToken cancellation);

    /// <summary>
    /// Reads the output of a command task.
    /// </summary>
    /// <param name="id">The task.</param>
    /// <param name="lines">The most lines to return, counted from the end.</param>
    /// <returns>The output.</returns>
    /// <exception cref="InvalidOperationException">The identifier is unknown or the task is a helper, which has no output before it ends.</exception>
    string Output(string id, int lines);

    /// <summary>
    /// Stops a task.
    /// </summary>
    /// <param name="id">The task.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="InvalidOperationException">The identifier is unknown.</exception>
    string Stop(string id);

    /// <summary>
    /// Stops every task that runs, without waiting for them to end.
    /// </summary>
    void StopAll();
}

/// <inheritdoc />
/// <param name="time">The clock the times of the tasks, their time limits and the waits run on, or <see langword="null" /> for the system's.</param>
internal sealed class PromptTasks(TimeProvider? time = null) : IPromptTasks
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private readonly object gate = new();

    private readonly Dictionary<string, BackgroundTask> tasks = [];

    private readonly ShellProcess shell = new();

    private readonly int limit = 16;

    private readonly int keepEnded = 20;

    private readonly int outputLimit = 64 * 1024;

    private readonly int tailLines = 8;

    private readonly int reportLines = 100;

    private readonly int maximumWait = 3600;

    private readonly TimeSpan batchGrace = TimeSpan.FromMilliseconds(300);

    private int counter;

    /// <inheritdoc />
    public string StartCommand(string workingFolder, string command, int seconds)
    {
        BackgroundTask task = Register(command);
        ShellOutput output = new(outputLimit);
        task.Output = output;

        try
        {
            Process process = shell.Start(workingFolder, command);
            _ = Run(task, process, output, seconds);
        }
        catch
        {
            Forget(task);
            throw;
        }

        return $"Started {task.Id} in the background. It keeps running while you do other things. {Follow()}";
    }

    /// <inheritdoc />
    public string StartHelper(string description, Func<CancellationToken, Task<string>> run)
    {
        BackgroundTask task = Register(description);
        _ = Run(task, run);
        return $"Started {task.Id} in the background: a helper works on the task and keeps going while you do other things. {Follow()}";
    }

    /// <inheritdoc />
    public async Task<string> Wait(IReadOnlyList<string>? ids, int timeoutSeconds, CancellationToken cancellation)
    {
        List<BackgroundTask> watched = Select(ids);

        if (watched.Count == 0)
        {
            return "There are no background tasks to wait for.";
        }

        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task timeUp = Task.Delay(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, maximumWait)), clock, waiting.Token).ContinueWith(static _ => { }, CancellationToken.None);

        try
        {
            while (true)
            {
                List<Task> running = [.. watched.Where(task => !task.IsFinished).Select(task => task.Completion.Task)];

                if (timeUp.IsCompleted || running.Count == 0 || watched.Any(task => task.IsFinished && !task.IsReported))
                {
                    break;
                }

                await Task.WhenAny(running.Append(timeUp));
            }
        }
        finally
        {
            await waiting.CancelAsync();
        }

        cancellation.ThrowIfCancellationRequested();

        if (watched.Any(task => task.IsFinished && !task.IsReported) && watched.Any(task => !task.IsFinished))
        {
            await Task.Delay(batchGrace, clock, CancellationToken.None);
        }

        string report = Report(watched);

        lock (gate)
        {
            Prune();
        }

        return report;
    }

    /// <inheritdoc />
    public string Output(string id, int count)
    {
        BackgroundTask task = Find(id);

        if (task.Output is not { } output)
        {
            throw new InvalidOperationException($"{id} is a helper, which has no output before it ends; wait for it with task_wait.");
        }

        string text = output.Tail(Math.Clamp(count, 1, 10000));
        return text.Length == 0 ? $"({id} has written nothing yet)" : text;
    }

    /// <inheritdoc />
    public string Stop(string id)
    {
        BackgroundTask task = Find(id);

        if (task.IsFinished)
        {
            return $"{id} had already ended.";
        }

        task.IsStopped = true;
        task.Cancel();
        return $"Stopping {id}.";
    }

    /// <inheritdoc />
    public void StopAll()
    {
        List<BackgroundTask> running;

        lock (gate)
        {
            running = [.. tasks.Values.Where(task => !task.IsFinished)];
        }

        foreach (BackgroundTask task in running)
        {
            task.IsStopped = true;
            task.Cancel();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        StopAll();
        List<Task> ending;

        lock (gate)
        {
            ending = [.. tasks.Values.Select(task => task.Completion.Task)];
        }

        await Task.WhenAny(Task.WhenAll(ending), Task.Delay(TimeSpan.FromSeconds(5), clock));
    }

    private BackgroundTask Register(string description)
    {
        lock (gate)
        {
            if (tasks.Values.Count(task => !task.IsFinished) >= limit)
            {
                throw new InvalidOperationException($"{limit} background tasks are running already; wait for some to finish or stop them.");
            }

            Prune();
            string id = $"task-{++counter}";
            BackgroundTask task = new(id, description, clock) { Number = counter };
            tasks[id] = task;
            return task;
        }
    }

    private void Forget(BackgroundTask task)
    {
        lock (gate)
        {
            tasks.Remove(task.Id);
        }
    }

    private async Task Run(BackgroundTask task, Process process, ShellOutput output, int seconds)
    {
        using (process)
        {
            try
            {
                Task readers = shell.Read(process, output);
                task.OnCancel = () => shell.Stop(process);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(seconds), clock);
                using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, task.Source.Token);
                bool timedOut = false;

                try
                {
                    await process.WaitForExitAsync(either.Token);
                }
                catch (OperationCanceledException)
                {
                    shell.Stop(process);
                    timedOut = timeout.IsCancellationRequested && !task.IsStopped;
                }

                await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(2), clock));
                task.Finish(task.IsStopped ? "was stopped" : timedOut ? $"timed out and was stopped (limit {seconds} seconds)" : $"ended with exit code {process.ExitCode}");
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                shell.Stop(process);
                output.Add(exception.Message);
                task.Finish("failed");
            }
        }
    }

    private async Task Run(BackgroundTask task, Func<CancellationToken, Task<string>> run)
    {
        try
        {
            string result = await Task.Run(() => run(task.Source.Token), CancellationToken.None);
            task.Result = result;
            task.Finish("finished");
        }
        catch (OperationCanceledException)
        {
            task.Finish("was stopped");
        }
        catch (Exception exception)
        {
            task.Result = exception.Message;
            task.Finish("failed");
        }
    }

    private List<BackgroundTask> Select(IReadOnlyList<string>? ids)
    {
        lock (gate)
        {
            if (ids is not { Count: > 0 })
            {
                return [.. tasks.Values.Where(task => !task.IsReported || !task.IsFinished)];
            }

            return [.. ids.Select(id => tasks.TryGetValue(id.Trim(), out BackgroundTask? found) ? found : throw Unknown(id))];
        }
    }

    private BackgroundTask Find(string id)
    {
        lock (gate)
        {
            return tasks.TryGetValue(id.Trim(), out BackgroundTask? found) ? found : throw Unknown(id);
        }
    }

    private void Prune()
    {
        List<BackgroundTask> ended = [.. tasks.Values.Where(task => task.IsFinished && task.IsReported).OrderBy(task => task.Number)];

        foreach (BackgroundTask old in ended.Take(Math.Max(0, ended.Count - keepEnded)))
        {
            tasks.Remove(old.Id);
            old.Release();
        }
    }

    private InvalidOperationException Unknown(string id)
        => id.Trim().StartsWith("task-", StringComparison.Ordinal) && int.TryParse(id.Trim()[5..], NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1 && number <= counter
            ? new InvalidOperationException($"{id.Trim()} ended and was cleared: only the {keepEnded} latest ended tasks are kept once they have been reported.")
            : new InvalidOperationException($"There is no task {id}.");

    private string Follow()
        => "Use task_wait with a timeout to hear when it ends while you keep writing status updates, task_output to read what it wrote and task_stop to end it. Wait for every task to end before you reply.";

    private string Report(List<BackgroundTask> watched)
    {
        List<string> parts = [];

        foreach (BackgroundTask task in watched.Where(task => task.IsFinished && !task.IsReported))
        {
            task.IsReported = true;
            string body = task.Output is { } output ? output.Tail(reportLines) : task.Result ?? string.Empty;
            string cut = task.Output is { IsCut: true } ? " The output reached its limit." : string.Empty;
            parts.Add($"{task.Id} {task.Outcome} after {Span(task.Elapsed)}.{cut}{(body.Length == 0 ? string.Empty : $"\n{body}")}");
        }

        bool timedOut = parts.Count == 0;

        foreach (BackgroundTask task in watched.Where(task => !task.IsFinished))
        {
            string last = task.Output is { } output ? output.Tail(tailLines) : string.Empty;
            string described = task.Description.ReplaceLineEndings(" ");
            parts.Add($"{task.Id} is still running after {Span(task.Elapsed)} ({described[..Math.Min(80, described.Length)]}).{(last.Length == 0 ? string.Empty : $" Latest output:\n{last}")}");
        }

        string report = parts.Count == 0 ? "Nothing new: every task you asked about was already reported." : string.Join("\n\n", parts);
        return timedOut && parts.Count > 0 ? $"{report}\n\nThe time was up and no task has ended. If you owe the user a status update, write it now as a normal message (what you have done and what comes next) before you wait again." : report;
    }

    private string Span(TimeSpan time)
        => time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min {time.Seconds} s" : $"{Math.Max(1, (int)time.TotalSeconds)} s";

    private sealed class BackgroundTask(string id, string description, TimeProvider clock)
    {
        private readonly DateTimeOffset started = clock.GetUtcNow();

        private readonly object sync = new();

        private DateTimeOffset? ended;

        public string Id { get; } = id;

        public int Number { get; init; }

        public string Description { get; } = description;

        public ShellOutput? Output { get; set; }

        public string? Result { get; set; }

        public string Outcome { get; private set; } = string.Empty;

        public bool IsStopped { get; set; }

        public bool IsReported { get; set; }

        public Action? OnCancel { get; set; }

        public CancellationTokenSource Source { get; } = new();

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsFinished => Completion.Task.IsCompleted;

        public TimeSpan Elapsed => (ended ?? clock.GetUtcNow()) - started;

        public void Release()
            => Source.Dispose();

        public void Cancel()
        {
            Source.Cancel();
            OnCancel?.Invoke();
        }

        public void Finish(string outcome)
        {
            lock (sync)
            {
                if (IsFinished)
                {
                    return;
                }

                Outcome = outcome;
                ended = clock.GetUtcNow();
            }

            Completion.TrySetResult();
        }
    }
}
