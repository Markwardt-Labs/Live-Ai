namespace Markwardt.LiveAi;

/// <summary>
/// Runs a engine's process for one prompt and reports what it writes.
/// </summary>
internal interface ITurnRunner
{
    /// <summary>
    /// Starts the process, writes the prompt to it and reports what it does until it exits.
    /// </summary>
    /// <param name="engine">The engine that reads what the process writes.</param>
    /// <param name="start">The process to start; its streams are redirected here.</param>
    /// <param name="prompt">The prompt, written to the standard input.</param>
    /// <param name="cancellation">Ends the process and stops reading.</param>
    /// <returns>The events in order; a <see cref="PromptEngineEventKind.Failed" /> event is added when the process failed without reporting why.</returns>
    /// <exception cref="PromptSessionException">The process could not be started.</exception>
    IAsyncEnumerable<PromptEngineEvent> Run(IPromptEngine engine, ProcessStartInfo start, string prompt, CancellationToken cancellation = default);

    /// <summary>
    /// Starts a process, writes some lines to it and reports the lines it writes until it exits or the enumeration is stopped, in which case it is ended.
    /// </summary>
    /// <param name="name">The name of the tool, used in messages.</param>
    /// <param name="start">The process to start; its streams are redirected here.</param>
    /// <param name="input">The lines to write to the standard input, in order.</param>
    /// <param name="closeInput">Whether to close the standard input after the lines, for a tool that reads until the end of its input.</param>
    /// <param name="cancellation">Ends the process and stops reading.</param>
    /// <returns>The lines of the standard output.</returns>
    /// <exception cref="PromptSessionException">The process could not be started, or it ended on its own with a failure.</exception>
    IAsyncEnumerable<string> Exchange(string name, ProcessStartInfo start, IReadOnlyList<string> input, bool closeInput, CancellationToken cancellation = default);

    /// <summary>
    /// Starts a process that takes any number of prompts.
    /// </summary>
    /// <param name="engine">The engine that frames the prompts and reads what the process writes.</param>
    /// <param name="start">The process to start; its streams are redirected here.</param>
    /// <param name="given">What the process is started for.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="PromptToolStartException">The process could not be started.</exception>
    IPromptProcess Open(IPromptEngine engine, ProcessStartInfo start, PromptEngineStart given);
}

/// <inheritdoc />
internal sealed class TurnRunner : ITurnRunner
{
    private readonly UTF8Encoding utf8 = new(false);

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptEngineEvent> Run(IPromptEngine engine, ProcessStartInfo start, string prompt, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        using Process process = Launch(engine.Identifier, Redirect(start));

        try
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            bool failed = false;

            await Write(process, prompt, cancellation);

            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                foreach (PromptEngineEvent output in engine.Parse(line))
                {
                    failed |= output.Kind == PromptEngineEventKind.Failed;
                    yield return output;
                }
            }

            await process.WaitForExitAsync(cancellation);

            if (process.ExitCode != 0 && !failed)
            {
                string detail = (await errors).Trim();
                yield return new PromptEngineEvent { Kind = PromptEngineEventKind.Failed, Text = detail.Length > 0 ? detail : $"{engine.Identifier} stopped." };
            }
        }
        finally
        {
            Kill(process);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> Exchange(string name, ProcessStartInfo start, IReadOnlyList<string> input, bool closeInput, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        using Process process = Launch(name, Redirect(start));

        try
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                foreach (string line in input)
                {
                    await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellation);
                }

                await process.StandardInput.FlushAsync(cancellation);

                if (closeInput)
                {
                    process.StandardInput.Close();
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }

            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                yield return line;
            }

            await process.WaitForExitAsync(cancellation);

            if (process.ExitCode != 0)
            {
                string detail = (await errors).Trim();
                throw new PromptSessionException(detail.Length > 0 ? detail : $"{name} stopped.");
            }
        }
        finally
        {
            Kill(process);
        }
    }

    /// <inheritdoc />
    public IPromptProcess Open(IPromptEngine engine, ProcessStartInfo start, PromptEngineStart given)
        => engine.Attach(Launch(engine.Identifier, Redirect(start)), given);

    private ProcessStartInfo Redirect(ProcessStartInfo start)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = utf8;
        start.StandardOutputEncoding = utf8;
        start.StandardErrorEncoding = utf8;
        return start;
    }

    private Process Launch(string name, ProcessStartInfo start)
    {
        Process process = new() { StartInfo = start };

        try
        {
            process.Start();
            return process;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new PromptToolStartException($"{name} could not be started ('{start.FileName}'): {exception.Message}");
        }
    }

    private async Task Write(Process process, string prompt, CancellationToken cancellation = default)
    {
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellation);
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }
}
