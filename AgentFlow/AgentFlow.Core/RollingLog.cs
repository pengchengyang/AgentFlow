// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="RollingLog.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Rolling.Logging;
using Serilog.Events;

namespace AgentFlow.Core;

/// <summary>
/// Solution-wide setup for the <c>Rolling.Logging</c> component. Any host (Desktop, CLI, Web,
/// tests) may call <see cref="Configure"/> once to customise the sinks / level / file path.
/// If no host calls it, <see cref="RollingLoggerProvider"/> lazily auto-initialises with the
/// defaults so every project that logs through <c>ILogger</c> writes via Rolling.Logging.
/// </summary>
public static class RollingLog
{
    private static readonly object Gate = new();
    private static bool _configured;

    /// <summary>Shared default options (console + rolling file, Debug level).</summary>
    public static RollingLoggerOptions DefaultOptions() => new()
    {
        MinimumLevel = LogEventLevel.Debug,
        LogFilePath = "./Logs/Rolling-.log",
        OutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}][{Level:u3}][{SourceContext}][{CallerFile}:{CallerLineNumber}] {Message:lj}{NewLine}{Exception}",
        FileSizeLimitBytes = 1024 * 1024 * 10,
        RetainedFileCountLimit = 5,
        EnableConsole = true,
        EnableFile = true,
    };

    /// <summary>Initialise Rolling.Logging once with (optionally) customised options.</summary>
    public static void Configure(Action<RollingLoggerOptions>? configure = null)
    {
        lock (Gate)
        {
            if (_configured) return;
            var options = DefaultOptions();
            configure?.Invoke(options);
            RollingLogger.Initialize(options);
            _configured = true;
        }
    }

    /// <summary>Ensure Rolling.Logging is initialised (idempotent; used by the logger provider).</summary>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_configured) return;
            RollingLogger.Initialize(DefaultOptions());
            _configured = true;
        }
    }
}
