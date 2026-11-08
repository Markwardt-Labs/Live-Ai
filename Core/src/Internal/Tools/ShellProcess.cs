namespace Markwardt.LiveAi;

/// <summary>
/// Starts, reads and stops the processes of shell commands for the shell tool and for background tasks.
/// </summary>
internal sealed class ShellProcess
{
    /// <summary>
    /// Starts a command with the system shell, with no input to wait for.
    /// </summary>
    /// <param name="workingFolder">The folder to run in.</param>
    /// <param name="command">The command line.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="InvalidOperationException">The command could not be started.</exception>
    public Process Start(string workingFolder, string command)
    {
        ProcessStartInfo start = new(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            WorkingDirectory = workingFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(command);
        Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"The command could not be started: {exception.Message}");
        }

        process.StandardInput.Close();
        return process;
    }

    /// <summary>
    /// Reads the standard output and error of a process into an output until both end.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <param name="output">Receives the lines.</param>
    /// <returns>A task that completes when both streams end.</returns>
    public Task Read(Process process, ShellOutput output)
        => Task.WhenAll(Pump(process.StandardOutput, output), Pump(process.StandardError, output));

    /// <summary>
    /// Kills a process and everything it started, if it still runs.
    /// </summary>
    /// <param name="process">The process.</param>
    public void Stop(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private async Task Pump(StreamReader reader, ShellOutput output)
    {
        for (string? line = await reader.ReadLineAsync(); line is not null; line = await reader.ReadLineAsync())
        {
            output.Add(line);
        }
    }
}
