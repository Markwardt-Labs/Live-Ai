namespace Markwardt.LiveAi.Tests;

public sealed class ListLogger : ILogger
{
    private readonly object gate = new();

    private readonly List<(LogLevel Level, string Message, Exception? Exception)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (gate)
            {
                return [.. entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel)
        => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (gate)
        {
            entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
