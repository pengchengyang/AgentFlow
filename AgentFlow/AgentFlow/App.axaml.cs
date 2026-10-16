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
    /// <summary>当前是否为深色主题。</summary>
    public static bool IsDarkTheme { get; private set; } = false;

    /// <summary>切换浅色/深色主题。</summary>
    public static void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var app = Current as App;
        if (app == null) return;

        // 清空资源字典，重新加载对应主题
        app.Resources.MergedDictionaries.Clear();

        var uri = IsDarkTheme
            ? new Uri("avares://AgentFlow/Themes/Colors.Dark.axaml")
            : new Uri("avares://AgentFlow/Themes/Colors.Light.axaml");

        app.Resources.MergedDictionaries.Add(
            (ResourceDictionary)AvaloniaXamlLoader.Load(uri));

        // 触发主窗口里的 GraphCanvas 重绘(画布是代码自绘的,不会自动刷新)
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