// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Program.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Avalonia;
using Hosting.Logging;

namespace Demo.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.ConfigureHost("DemoDesktop");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.Close();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
