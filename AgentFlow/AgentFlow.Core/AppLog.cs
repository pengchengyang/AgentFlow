// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="AppLog.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Reflection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace AgentFlow.Core;

/// <summary>
/// Solution-wide Serilog bootstrap, replacing the previous <c>Rolling.Logging</c> wrapper.
/// There is exactly one Serilog <see cref="Log.Logger"/> per process; every project (Core, UI,
/// plugins) logs through <see cref="ILogger"/> and is routed here via <c>AddSerilog</c>.
/// A host calls <see cref="ConfigureHost"/> once at startup to choose its log file; all in-process
/// projects then share that single file. If a host forgets to call it, the name is derived from the
/// entry assembly so no stray "Rolling-.log" is ever produced.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static bool _configured;

    /// <summary>Console + rolling-file template. SourceContext keeps in-file attribution per module/node.</summary>
    private const string Template =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}][{LevelName}][{SourceContext}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Shared default options (console + rolling file, Verbose level, 10MB &times; 5).</summary>
    public static LoggerConfiguration DefaultConfiguration(string hostName)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.With(new MsLevelNameEnricher())
            .WriteTo.Console(outputTemplate: Template)
            .WriteTo.File(
                path: $"./Logs/AgentFlow-{hostName}-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 1024 * 1024 * 10,
                retainedFileCountLimit: 5,
                outputTemplate: Template);
    }

    /// <summary>
    /// Initialise the process-wide Serilog logger once with the given host name, choosing the log file.
    /// <paramref name="configure"/> lets a host add extra sinks (e.g. the UI panel sink in the Desktop app).
    /// </summary>
    public static void ConfigureHost(string hostName, Action<LoggerConfiguration>? configure = null)
    {
        lock (Gate)
        {
            if (_configured) return;
            var cfg = DefaultConfiguration(hostName);
            configure?.Invoke(cfg);
            Log.Logger = cfg.CreateLogger();
            _configured = true;
        }
    }

    /// <summary>
    /// Create an <see cref="ILoggerFactory"/> whose loggers all write to the single process-wide
    /// Serilog <see cref="Log.Logger"/> (Console + rolling file + any host-added sinks).
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

    /// <summary>Flush and close the process-wide Serilog logger (call in a host's finally block).</summary>
    public static void Close() => Log.CloseAndFlush();

    private static void EnsureConfigured()
    {
        lock (Gate)
        {
            if (_configured) return;
            // Fallback so a host that forgot ConfigureHost still writes to its own file,
            // never to a generic "Rolling-.log". "AgentFlow.Desktop" -> "AgentFlow-Desktop-.log".
            var entry = Assembly.GetEntryAssembly()?.GetName().Name ?? "AgentFlow";
            var host = entry.StartsWith("AgentFlow.", StringComparison.Ordinal)
                ? entry["AgentFlow.".Length..]
                : entry;
            ConfigureHost(host);
        }
    }
}

/// <summary>
/// Renders the level using Microsoft.Extensions.Logging naming (TRACE/DEBUG/INFO/WARN/ERROR/FATAL)
/// so the file/console match the UI panel instead of Serilog's VRB/DBG/INF/WRN/ERR/FTL.
/// </summary>
internal sealed class MsLevelNameEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var name = logEvent.Level switch
        {
            LogEventLevel.Verbose => "TRACE",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARN",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => "INFO",
        };
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("LevelName", name));
    }
}
