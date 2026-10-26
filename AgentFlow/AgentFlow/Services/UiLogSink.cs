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

    /// <summary>Single-line message used for grid display (all whitespace flattened to one space).</summary>
    public required string Message { get; init; }

    /// <summary>Original multi-line message preserved for clipboard/tooltip copy.</summary>
    public required string RawMessage { get; init; }

    /// <summary>Flatten any whitespace (newlines, tabs) into single spaces for single-line display.</summary>
    public static string Flatten(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString().Trim();
    }
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
    private static int _started;

    public UiLogSink()
    {
        // Ensure only one live sink emits, even if a host or debugger creates several.
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("UiLogSink may only be created once.");
    }

    public void Emit(LogEvent logEvent)
    {
        var raw = logEvent.RenderMessage();
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
            Message = UiLogEntry.Flatten(raw),
            RawMessage = raw,
        };
        UiLogStore.Add(entry);
    }
}
