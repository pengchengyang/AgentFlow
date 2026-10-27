// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="AppLog.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Reflection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Hosting.Logging;

/// <summary>
/// Process-wide Serilog bootstrap, reusable by any application (no AgentFlow/UI dependency).
/// There is exactly one Serilog <see cref="Log.Logger"/> per process; every project logs through
/// <see cref="ILogger"/> and is routed here via <c>AddSerilog</c>.
/// <para>
/// A host calls <see cref="ConfigureHost"/> once at startup to choose the product name (and optionally
/// the host name). The log file is <c>./Logs/{appName}-{hostName}-.log</c>, e.g.
/// <c>Logs/AgentFlow-Desktop-.log</c>, so the same library can be reused by any product without
/// hard-coded names. If a host forgets to call it, both names are derived from the entry assembly.
/// </para>
/// <para>
/// For hosts that run several workloads in one process (e.g. a CLI hosting N workflow instances),
/// <see cref="CreateLoggerFactory(string)"/> returns a factory whose messages carry a <c>[tag]</c>
/// prefix, so every line can be attributed to its instance while still sharing one log file.
/// </para>
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static bool _configured;
    private static string? _currentFile;

    /// <summary>
    /// Console + rolling-file template. <c>{Tag}</c> renders nothing unless a tagged logger (see
    /// <see cref="CreateLoggerFactory(string)"/>) produced the event, in which case it inserts
    /// <c> [a.json#2]</c> after the source context. SourceContext keeps in-file attribution per module/node.
    /// </summary>
    private const string Template =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}][{LevelName}][{SourceContext}]{Tag} {Message:lj}{NewLine}{Exception}";

    /// <summary>Directory (relative to the working directory) that receives the rolling log files.</summary>
    public const string DirectoryName = "Logs";

    /// <summary>The log file the process is currently writing (null until the logger is configured).</summary>
    public static string? CurrentFile
    {
        get { lock (Gate) return _currentFile; }
    }

    /// <summary>
    /// Shared default options (console + rolling file, Verbose level, 10 MB &times; 5) writing to
    /// <paramref name="logFilePath"/>. Exposed so a host can build on it (e.g. add sinks).
    /// </summary>
    public static LoggerConfiguration DefaultConfiguration(string logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.With(new MsLevelNameEnricher())
            .WriteTo.Console(outputTemplate: Template)
            .WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 1024 * 1024 * 10,
                retainedFileCountLimit: 5,
                outputTemplate: Template);
    }

    /// <summary>
    /// Initialise the process-wide Serilog logger once.
    /// </summary>
    /// <param name="appName">Product name, used as the file name prefix: <c>{appName}-{hostName}-.log</c>.</param>
    /// <param name="hostName">Host/entry-point name (defaults to the entry assembly short name, e.g. "Desktop").</param>
    /// <param name="configure">Lets a host add extra sinks (e.g. an in-memory sink feeding a UI panel).</param>
    public static void ConfigureHost(
        string appName,
        string? hostName = null,
        Action<LoggerConfiguration>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        lock (Gate)
        {
            if (_configured) return;

            var host = string.IsNullOrWhiteSpace(hostName) ? DefaultHostName() : hostName!.Trim();
            var file = Path.Combine(".", DirectoryName, $"{Sanitize(appName)}-{Sanitize(host)}-.log");
            var cfg = DefaultConfiguration(file);
            configure?.Invoke(cfg);
            Log.Logger = cfg.CreateLogger();
            _currentFile = file;
            _configured = true;
        }
    }

    /// <summary>
    /// Create an <see cref="ILoggerFactory"/> whose loggers all write to the single process-wide
    /// Serilog <see cref="Log.Logger"/> (console + rolling file + any host-added sinks).
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory()
    {
        EnsureConfigured();
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddSerilog();
        });
    }

    /// <summary>
    /// Create an <see cref="ILoggerFactory"/> whose lines are tagged with <c>[tag]</c> right after the
    /// source context, so several workloads sharing one process (e.g. multiple workflow instances) stay
    /// distinguishable in the same log file. Implemented as a Serilog child logger carrying the
    /// <c>Tag</c> property: no message text is rewritten, and untagged lines are unaffected.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        EnsureConfigured();
        return new SerilogLoggerFactory(Log.Logger.ForContext("Tag", $" [{tag.Trim()}]"));
    }

    /// <summary>Flush and close the process-wide Serilog logger (call in a host's finally block).</summary>
    public static void Close() => Log.CloseAndFlush();

    private static void EnsureConfigured()
    {
        lock (Gate)
        {
            if (_configured) return;
        }

        // Fallback so a host that forgot ConfigureHost still writes to its own file,
        // never to a generic name. "AgentFlow.Desktop" -> app "AgentFlow", host "Desktop".
        var entry = Assembly.GetEntryAssembly()?.GetName().Name ?? "App";
        var dot = entry.IndexOf('.');
        var app = dot > 0 ? entry[..dot] : entry;
        var host = dot > 0 && dot + 1 < entry.Length ? entry[(dot + 1)..] : DefaultHostName();
        ConfigureHost(app, host);
    }

    /// <summary>The entry assembly's short name, e.g. "Desktop" for "AgentFlow.Desktop".</summary>
    private static string DefaultHostName()
    {
        var entry = Assembly.GetEntryAssembly()?.GetName().Name ?? "Host";
        var dot = entry.IndexOf('.');
        return dot > 0 && dot + 1 < entry.Length ? entry[(dot + 1)..] : entry;
    }

    private static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }
}

/// <summary>
/// Renders the level using Microsoft.Extensions.Logging naming (see <see cref="LogLevelNames"/>)
/// so the file/console match the UI panel instead of Serilog's VRB/DBG/INF/WRN/ERR/FTL.
/// </summary>
internal sealed class MsLevelNameEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("LevelName", LogLevelNames.From(logEvent.Level)));
    }
}
