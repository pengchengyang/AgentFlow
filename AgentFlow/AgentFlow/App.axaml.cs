// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="App.axaml.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.ViewModels;
using AgentFlow.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using System.Linq;

namespace AgentFlow;

public partial class App : Application
{
    /// <summary>Whether the current theme is dark.</summary>
    public static bool IsDarkTheme { get; private set; } = false;

    /// <summary>Toggles between light and dark themes.</summary>
    public static void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var app = Current as App;
        if (app == null) return;

        // Clear the resource dictionary and reload the corresponding theme
        app.Resources.MergedDictionaries.Clear();

        var uri = IsDarkTheme
            ? new Uri("avares://AgentFlow/Themes/Colors.Dark.axaml")
            : new Uri("avares://AgentFlow/Themes/Colors.Light.axaml");

        app.Resources.MergedDictionaries.Add(
            (ResourceDictionary)AvaloniaXamlLoader.Load(uri));

        // Trigger a redraw of the GraphCanvas in the main window (the canvas is drawn in code and does not refresh automatically)
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            foreach (var canvas in desktop.MainWindow.GetVisualDescendants().OfType<Views.GraphCanvas>())
            {
                canvas.InvalidateVisual();
            }
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}