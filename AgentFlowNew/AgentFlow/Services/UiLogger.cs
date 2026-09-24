// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="UiLogger.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Services;

/// <summary>Writes engine logs into the UI log panel (marshals to the UI thread).</summary>
public sealed class UiLogger(ObservableCollection<string> lines) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{logLevel}] {formatter(state, exception)}";
        Dispatcher.UIThread.Post(() =>
        {
            lines.Add(line);
            if (lines.Count > 1000) lines.RemoveAt(0);
        });
        if (exception is not null)
            Dispatcher.UIThread.Post(() => lines.Add(exception.ToString()));
    }
}

public sealed class UiLoggerProvider(ObservableCollection<string> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new UiLogger(lines);
    public void Dispose() { }
}
