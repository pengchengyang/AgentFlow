// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="UiLogSink.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Hosting.Logging;
using Serilog.Core;
using Serilog.Events;

namespace AgentFlow.Services;

/// <summary>One row in the UI log panel.</summary>
public sealed class UiLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Source { get; init; }

    /// <summary>RunId tag carried by a tenant/run-scoped logger; empty for host-level logs.</summary>
    public required string RunId { get; init; }

    /// <summary>Raw tag text from Serilog, for example <c>[run-123]</c>.</summary>
    public required string Tag { get; init; }

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
    private const int MaxEntries = 2000;
    private const int MaxErrorsPerRun = 50;

    private static readonly ConcurrentQueue<UiLogEntry> PendingEntries = new();
    private static readonly object ErrorGate = new();
    private static readonly Dictionary<string, Queue<UiLogEntry>> RunErrors =
        new(StringComparer.OrdinalIgnoreCase);
    private static int _flushScheduled;

    public static ObservableCollection<UiLogEntry> Entries { get; } = new();

    public static void Add(UiLogEntry entry)
    {
        PendingEntries.Enqueue(entry);
        ScheduleFlush();
    }

    /// <summary>Return the retained error entries for one run, oldest first.</summary>
    public static IReadOnlyList<UiLogEntry> GetRunErrors(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return Array.Empty<UiLogEntry>();

        lock (ErrorGate)
        {
            return RunErrors.TryGetValue(runId, out var errors)
                ? errors.ToArray()
                : Array.Empty<UiLogEntry>();
        }
    }

    public static void Clear()
    {
        Dispatcher.UIThread.Post(() =>
        {
            while (PendingEntries.TryDequeue(out _)) { }
            Entries.Clear();
            lock (ErrorGate)
                RunErrors.Clear();
        });
    }

    private static void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(FlushPending, DispatcherPriority.Background);
    }

    private static void FlushPending()
    {
        try
        {
            while (PendingEntries.TryDequeue(out var entry))
            {
                Entries.Add(entry);
                RetainRunError(entry);
            }

            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);
        }
        finally
        {
            Volatile.Write(ref _flushScheduled, 0);
            if (!PendingEntries.IsEmpty)
                ScheduleFlush();
        }
    }

    private static void RetainRunError(UiLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RunId)
            || !string.Equals(entry.Level, "ERROR", StringComparison.OrdinalIgnoreCase))
            return;

        lock (ErrorGate)
        {
            if (!RunErrors.TryGetValue(entry.RunId, out var errors))
            {
                errors = new Queue<UiLogEntry>();
                RunErrors[entry.RunId] = errors;
            }

            errors.Enqueue(entry);
            while (errors.Count > MaxErrorsPerRun)
                errors.Dequeue();
        }
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
        var tag = logEvent.Properties.TryGetValue("Tag", out var tagProperty)
            ? tagProperty.ToString().Trim('"').Trim()
            : "";
        var runId = tag;
        if (runId.StartsWith('[') && runId.EndsWith(']'))
            runId = runId[1..^1].Trim();

        var entry = new UiLogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = LogLevelNames.From(logEvent.Level),
            Source = logEvent.Properties.TryGetValue("SourceContext", out var sc)
                ? sc.ToString().Trim('"') : "",
            RunId = runId,
            Tag = tag,
            Message = UiLogEntry.Flatten(raw),
            RawMessage = raw,
        };
        UiLogStore.Add(entry);
    }
}