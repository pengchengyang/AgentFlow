using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>Minimal console ILogger (avoids an extra NuGet dependency).</summary>
public sealed class ConsoleLogger(string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{logLevel,-11}] [{category}] {formatter(state, exception)}";
        if (logLevel >= LogLevel.Error)
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
        if (exception is not null)
            Console.Error.WriteLine(exception);
    }
}

/// <summary>Creates ConsoleLoggers by category name.</summary>
public sealed class ConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
    public void Dispose() { }
}
