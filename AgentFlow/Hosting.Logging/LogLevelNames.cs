// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="LogLevelNames.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Serilog.Events;

namespace Hosting.Logging;

/// <summary>
/// Single source of truth for level labels. Microsoft.Extensions.Logging naming is used everywhere
/// (TRACE/DEBUG/INFO/WARN/ERROR/FATAL) so the console, the log file and any UI panel agree, instead of
/// Serilog's short VRB/DBG/INF/WRN/ERR/FTL.
/// </summary>
public static class LogLevelNames
{
    /// <summary>Map a Serilog level to its label.</summary>
    public static string From(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "TRACE",
        LogEventLevel.Debug => "DEBUG",
        LogEventLevel.Information => "INFO",
        LogEventLevel.Warning => "WARN",
        LogEventLevel.Error => "ERROR",
        LogEventLevel.Fatal => "FATAL",
        _ => "INFO",
    };

    /// <summary>Map a Microsoft.Extensions.Logging level to its label.</summary>
    public static string From(Microsoft.Extensions.Logging.LogLevel level) => level switch
    {
        Microsoft.Extensions.Logging.LogLevel.Trace => "TRACE",
        Microsoft.Extensions.Logging.LogLevel.Debug => "DEBUG",
        Microsoft.Extensions.Logging.LogLevel.Information => "INFO",
        Microsoft.Extensions.Logging.LogLevel.Warning => "WARN",
        Microsoft.Extensions.Logging.LogLevel.Error => "ERROR",
        Microsoft.Extensions.Logging.LogLevel.Critical => "FATAL",
        _ => "INFO",
    };
}
