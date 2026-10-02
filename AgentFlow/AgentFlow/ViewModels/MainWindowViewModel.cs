// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="MainWindowViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Reflection;

namespace AgentFlow.ViewModels;

/// <summary>
/// Root view-model for the main application window. Owns the shared <see cref="MainViewModel"/>
/// which the main content view binds to. Keeps the 1 view &lt;-&gt; 1 view-model mapping for MainWindow.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    public MainViewModel Main { get; }

    /// <summary>
    /// Application version, sourced from the entry assembly (AgentFlow.Desktop) whose
    /// version is defined in its project file. The informational version may carry a
    /// source-revision suffix ("1.0+<hash>"); only the plain version is kept.
    /// </summary>
    public string Version { get; } = TrimRevision(Assembly.GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion) ?? "1.0";

    private static string? TrimRevision(string? v)
    {
        if (string.IsNullOrEmpty(v)) return v;
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    public MainWindowViewModel()
    {
        Main = new MainViewModel();
    }
}