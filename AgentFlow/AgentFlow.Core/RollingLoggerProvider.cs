// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="RollingLoggerProvider.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using Rolling.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Routes every <see cref="ILogger"/> call (from Core, plugins, Desktop and the CLI) to the
/// <c>Rolling.Logging</c> component, which writes to console and rolling file sinks via Serilog.
/// Add this provider to an <see cref="ILoggerFactory"/>; it auto-initialises Rolling.Logging solution-wide.
/// </summary>
public sealed class RollingLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        // Whole-solution guarantee: any host that uses this provider is initialised, even if
        // it never called RollingLog.Configure itself (e.g. the Web host or a test project).
        RollingLog.EnsureInitialized();
        return new RollingLoggerAdapter(categoryName);
    }

    public void Dispose() { }
}

/// <summary>
/// Raised for every log event emitted through <see cref="RollingLoggerAdapter"/>.
/// UI layers can subscribe to show logs in an on-screen panel (must marshal to UI thread).
/// </summary>
public static class LogEvents
{
    public static event Action<LogLevel, string, string, Exception?>? OnLogEmitted;

    internal static void Raise(LogLevel level, string category, string message, Exception? exception)
        => OnLogEmitted?.Invoke(level, category, message, exception);
}

/// <summary>An <see cref="ILogger"/> adapter that forwards every level to <see cref="RollingLogger"/>.</summary>
public sealed class RollingLoggerAdapter(string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var prefixed = $"[{category}] {message}";

        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                RollingLogger.Debug(prefixed);
                break;
            case LogLevel.Information:
                RollingLogger.Info(prefixed);
                break;
            case LogLevel.Warning:
                RollingLogger.Warn(prefixed);
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                if (exception is not null)
                    RollingLogger.Error(exception, prefixed);
                else
                    RollingLogger.Error(prefixed);
                break;
            case LogLevel.None:
                break;
        }

        // Notify UI subscribers (fire-and-forget; exceptions in UI handlers must not break logging).
        try { LogEvents.Raise(logLevel, category, message, exception); } catch { /* swallow */ }
    }
}
