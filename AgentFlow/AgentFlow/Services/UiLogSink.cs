// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="UiLogSink.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Serilog.Core;
using Serilog.Events;

namespace AgentFlow.Services;

/// <summary>One row in the UI log panel.</summary>
public sealed class UiLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Shared in-memory log store for the UI panel. A single Serilog sink (<see cref="UiLogSink"/>)
/// writes into this collection; the ViewModel binds to <see cref="Entries"/>.
/// All updates are marshalled to the UI thread via <see cref="Dispatcher.UIThread"/>.
/// </summary>
public static class UiLogStore
{
    public static ObservableCollection<UiLogEntry> Entries { get; } = new();

    public static void Add(UiLogEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Entries.Add(entry);
            if (Entries.Count > 500) Entries.RemoveAt(0);
        });
    }

    public static void Clear()
    {
        Dispatcher.UIThread.Post(() => Entries.Clear());
    }
}

/// <summary>Serilog sink that forwards every event into <see cref="UiLogStore"/> for the UI panel.</summary>
public sealed class UiLogSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var entry = new UiLogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = logEvent.Level switch
            {
                LogEventLevel.Verbose => "TRACE",
                LogEventLevel.Debug => "DEBUG",
                LogEventLevel.Information => "INFO",
                LogEventLevel.Warning => "WARN",
                LogEventLevel.Error => "ERROR",
                LogEventLevel.Fatal => "FATAL",
                _ => logEvent.Level.ToString().ToUpperInvariant(),
            },
            Source = logEvent.Properties.TryGetValue("SourceContext", out var sc)
                ? sc.ToString().Trim('"') : "",
            Message = logEvent.RenderMessage(),
        };
        UiLogStore.Add(entry);
    }
}
