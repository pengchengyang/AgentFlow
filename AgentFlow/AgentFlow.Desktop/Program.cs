// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Program.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Threading;
using AgentFlow.Services;
using Hosting.Logging;
using Serilog;

namespace AgentFlow.Desktop;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // One log file for the whole Desktop process (UI + Core + all plugins):
        // ./Logs/AgentFlow-Desktop-.log, plus the UI panel sink that mirrors every log line into
        // the on-screen log panel.
        AppLog.ConfigureHost("AgentFlow", configure: cfg => cfg.WriteTo.Sink(new UiLogSink()));
        InstallGlobalExceptionHandlers();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.Close();
        }
    }

    /// <summary>
    /// Capture UI-dispatcher and process-level failures in the normal rolling log before the
    /// runtime terminates. Task exceptions are observed so they do not silently crash finalization.
    /// </summary>
    private static void InstallGlobalExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            LogCrash("Dispatcher", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                LogCrash("AppDomain", exception);
            else
                LogCrash("AppDomain", new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception."));
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception exception)
    {
        try
        {
            Log.ForContext("SourceContext", "UnhandledException")
                .Fatal(exception, "Unhandled {Source} exception. Log file: {LogFile}", source, AppLog.CurrentFile);
        }
        catch
        {
            // Crash reporting must never mask the original exception.
        }
    }
    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

}
